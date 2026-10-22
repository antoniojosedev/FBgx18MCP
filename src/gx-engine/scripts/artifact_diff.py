"""Read-only byte differential of two generated artifact trees (engine vs SDK).

Both roots are explicit arguments. Nothing is read outside them, nothing is
written inside them, and no bytes are normalised: a file is ``identical`` only
when size and SHA-256 are equal. The tool never opens a Knowledge Base, the SDK
or a database; it only reads files that already exist under the two roots.

Scope: only regular files are compared (and only those with a selected
extension when ``--extension`` is given). Directories are not entries, so an
empty directory, or a directory present in one tree only, is never reported.
``summary.selectionComplete`` is true only when no scanned file was excluded by
the extension selection on either side; exit 0 with ``selectionComplete=false``
means the selected extensions match, not that the trees match.

The ``--out`` report is written to a temporary file next to the destination and
moved into place with ``os.replace``, so a symlink or hardlink at the output
leaf is replaced rather than written through. Any reparse point (symlink,
junction, mount point, cloud placeholder) inside a tree is refused.

Exit status: 0 every compared file identical (and at least one compared),
1 at least one different/engine-only/sdk-only file, 2 usage or path error,
unreadable file, symlink/junction entry, or nothing selected to compare.
"""
import argparse
import hashlib
import json
import os
import sys
import tempfile

EXIT_IDENTICAL = 0
EXIT_DIFFERENCES = 1
EXIT_ERROR = 2
DEFAULT_MAX_FILES = 100000
CHUNK = 1 << 20
FILE_ATTRIBUTE_REPARSE_POINT = 0x400


class ArtifactDiffError(Exception):
    """A usage, path or filesystem condition that makes the comparison invalid."""


def _real(path):
    return os.path.normcase(os.path.realpath(path))


def _is_within(child, parent):
    try:
        return os.path.commonpath([child, parent]) == parent
    except ValueError:  # different drives
        return False


def _validate_roots(engine_root, sdk_root):
    resolved = {}
    for label, root in (("engine", engine_root), ("sdk", sdk_root)):
        if not root:
            raise ArtifactDiffError(f"{label} root is empty")
        if not os.path.isdir(root):
            raise ArtifactDiffError(f"{label} root is not an existing directory: {root}")
        resolved[label] = os.path.realpath(root)
    engine, sdk = _real(resolved["engine"]), _real(resolved["sdk"])
    if _is_within(engine, sdk) or _is_within(sdk, engine):
        raise ArtifactDiffError(
            f"engine and sdk roots must not be equal or nested: {engine_root} / {sdk_root}")
    return resolved


def _normalise_extensions(extensions):
    if not extensions:
        return None
    normalised = []
    for extension in extensions:
        if not extension.startswith(".") or len(extension) < 2 or any(
                separator in extension for separator in "/\\*?"):
            raise ArtifactDiffError(f"extension must look like '.cs': {extension!r}")
        normalised.append(extension.lower())
    return tuple(sorted(set(normalised)))


def _is_link(entry):
    """True for symlinks, junctions and any other Windows reparse point.

    The reparse attribute is read directly so detection does not depend on
    ``DirEntry.is_junction`` (Python 3.12+).
    """
    if entry.is_symlink():
        return True
    is_junction = getattr(entry, "is_junction", None)
    if is_junction and is_junction():
        return True
    attributes = getattr(entry.stat(follow_symlinks=False), "st_file_attributes", 0)
    return bool(attributes & FILE_ATTRIBUTE_REPARSE_POINT)


def _classify_entry(entry):
    """Return "link", "dir", "file" or "other"; any filesystem failure is an input error."""
    try:
        if _is_link(entry):
            return "link"
        if entry.is_dir(follow_symlinks=False):
            return "dir"
        if entry.is_file(follow_symlinks=False):
            return "file"
        return "other"
    except OSError as error:
        raise ArtifactDiffError(f"cannot inspect {entry.path}: {error}") from error


def _list_files(root, max_files):
    """Return {relative posix path: absolute path} for every regular file under root."""
    files = {}
    stack = [root]
    while stack:
        directory = stack.pop()
        try:
            with os.scandir(directory) as iterator:
                entries = list(iterator)
        except OSError as error:
            raise ArtifactDiffError(f"cannot list {directory}: {error}") from error
        for entry in entries:
            kind = _classify_entry(entry)
            if kind == "link":
                raise ArtifactDiffError(
                    f"symlink or junction entries are refused: {entry.path}")
            if kind == "dir":
                stack.append(entry.path)
            elif kind == "file":
                relative = os.path.relpath(entry.path, root).replace(os.sep, "/")
                files[relative] = entry.path
                if len(files) > max_files:
                    raise ArtifactDiffError(
                        f"more than {max_files} files under {root}; raise --max-files deliberately")
            else:
                raise ArtifactDiffError(f"unsupported entry type (not file/dir): {entry.path}")
    return files


def _fingerprint(path):
    digest = hashlib.sha256()
    size = 0
    try:
        with open(path, "rb") as handle:
            while True:
                chunk = handle.read(CHUNK)
                if not chunk:
                    break
                size += len(chunk)
                digest.update(chunk)
    except OSError as error:
        raise ArtifactDiffError(f"cannot read {path}: {error}") from error
    return size, digest.hexdigest()


def _select(files, extensions):
    if extensions is None:
        return dict(files), 0
    selected = {path: full for path, full in files.items() if path.lower().endswith(extensions)}
    return selected, len(files) - len(selected)


def compare_trees(engine_root, sdk_root, extensions=None, max_files=DEFAULT_MAX_FILES):
    """Classify every selected file as identical/different/engine-only/sdk-only."""
    resolved = _validate_roots(engine_root, sdk_root)
    extensions = _normalise_extensions(extensions)
    engine_all = _list_files(resolved["engine"], max_files)
    sdk_all = _list_files(resolved["sdk"], max_files)
    engine_files, engine_excluded = _select(engine_all, extensions)
    sdk_files, sdk_excluded = _select(sdk_all, extensions)

    identical, different, engine_only, sdk_only = [], [], [], []
    for path in sorted(set(engine_files) | set(sdk_files)):
        in_engine, in_sdk = path in engine_files, path in sdk_files
        if in_engine and in_sdk:
            engine_size, engine_hash = _fingerprint(engine_files[path])
            sdk_size, sdk_hash = _fingerprint(sdk_files[path])
            if engine_size == sdk_size and engine_hash == sdk_hash:
                identical.append({"path": path, "size": engine_size, "sha256": engine_hash})
            else:
                different.append({
                    "path": path,
                    "engineSize": engine_size, "engineSha256": engine_hash,
                    "sdkSize": sdk_size, "sdkSha256": sdk_hash,
                })
        elif in_engine:
            size, digest = _fingerprint(engine_files[path])
            engine_only.append({"path": path, "size": size, "sha256": digest})
        else:
            size, digest = _fingerprint(sdk_files[path])
            sdk_only.append({"path": path, "size": size, "sha256": digest})

    sdk_lower = {entry["path"].lower(): entry["path"] for entry in sdk_only}
    case_only = [{"engine": entry["path"], "sdk": sdk_lower[entry["path"].lower()]}
                 for entry in engine_only if entry["path"].lower() in sdk_lower]
    total = len(identical) + len(different) + len(engine_only) + len(sdk_only)
    return {
        "engineRoot": resolved["engine"],
        "sdkRoot": resolved["sdk"],
        "extensions": list(extensions) if extensions else None,
        "summary": {
            "compared": total,
            "identical": len(identical),
            "different": len(different),
            "engineOnly": len(engine_only),
            "sdkOnly": len(sdk_only),
            "selectionComplete": engine_excluded == 0 and sdk_excluded == 0,
            "engineFilesScanned": len(engine_all),
            "sdkFilesScanned": len(sdk_all),
            "engineFilesSelected": len(engine_files),
            "sdkFilesSelected": len(sdk_files),
            "engineExcludedByExtension": engine_excluded,
            "sdkExcludedByExtension": sdk_excluded,
        },
        "identical": identical,
        "different": different,
        "engineOnly": engine_only,
        "sdkOnly": sdk_only,
        "caseOnlyNameMatches": case_only,
    }


def exit_code_for(report):
    summary = report["summary"]
    if summary["compared"] == 0:
        return EXIT_ERROR
    if summary["different"] or summary["engineOnly"] or summary["sdkOnly"]:
        return EXIT_DIFFERENCES
    return EXIT_IDENTICAL


def _check_output_path(out_path, report):
    parent = _real(os.path.dirname(os.path.abspath(out_path)))
    for root in (report["engineRoot"], report["sdkRoot"]):
        if _is_within(parent, _real(root)):
            raise ArtifactDiffError(f"--out must be outside both compared trees: {out_path}")


def _write_report(out_path, report):
    """Write the JSON report atomically without following an output-leaf link."""
    destination = os.path.abspath(out_path)
    descriptor, temporary = tempfile.mkstemp(
        prefix=".artifact-diff-", suffix=".tmp", dir=os.path.dirname(destination))
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(report, handle, indent=2, sort_keys=True)
            handle.write("\n")
        os.replace(temporary, destination)
    except BaseException:
        try:
            os.unlink(temporary)
        except OSError:
            pass
        raise


def _parser():
    parser = argparse.ArgumentParser(
        description="Read-only byte differential of an engine artifact tree and an SDK artifact tree.")
    parser.add_argument("--engine", required=True, help="engine-generated output directory")
    parser.add_argument("--sdk", required=True, help="SDK-generated output directory")
    parser.add_argument("--extension", action="append", dest="extensions", metavar=".EXT",
                        help="compare only files with this extension (repeatable); default: all files")
    parser.add_argument("--max-files", type=int, default=DEFAULT_MAX_FILES,
                        help=f"per-tree file bound (default {DEFAULT_MAX_FILES})")
    parser.add_argument("--out", help="write the full JSON report (including identical files) here; "
                                      "written atomically; must be outside both trees")
    return parser


def main(argv=None, stdout=None):
    stdout = stdout or sys.stdout
    args = _parser().parse_args(argv)
    try:
        if args.max_files < 1:
            raise ArtifactDiffError("--max-files must be positive")
        report = compare_trees(args.engine, args.sdk, args.extensions, args.max_files)
        if args.out:
            _check_output_path(args.out, report)
    except ArtifactDiffError as error:
        print(f"artifact_diff: error: {error}", file=sys.stderr)
        return EXIT_ERROR
    if args.out:
        try:
            _write_report(args.out, report)
        except OSError as error:
            print(f"artifact_diff: error: cannot write {args.out}: {error}", file=sys.stderr)
            return EXIT_ERROR
    console = {key: value for key, value in report.items() if key != "identical"}
    stdout.write(json.dumps(console, indent=2, sort_keys=True) + "\n")
    return exit_code_for(report)


if __name__ == "__main__":
    sys.exit(main())
