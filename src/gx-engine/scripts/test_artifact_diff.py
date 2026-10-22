import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import artifact_diff
from artifact_diff import ArtifactDiffError, compare_trees, exit_code_for, main

SCRIPT = Path(__file__).resolve().parent / "artifact_diff.py"


def write(root, relative, data):
    path = Path(root) / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def snapshot(root):
    return sorted((str(p.relative_to(root)), p.read_bytes()) if p.is_file() else (str(p.relative_to(root)), None)
                  for p in Path(root).rglob("*"))


def make_junction(link, target):
    created = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                             capture_output=True, text=True)
    if created.returncode != 0:
        raise AssertionError(f"mklink /J failed: {created.stdout}{created.stderr}")


def make_directory_link(testcase, link, target):
    """Directory symlink, or a junction on Windows when symlinks need privilege."""
    try:
        os.symlink(target, link, target_is_directory=True)
    except (OSError, NotImplementedError):
        if os.name != "nt":
            testcase.skipTest("symlink creation unavailable")
        make_junction(link, target)


class HideIsJunction:
    """DirEntry proxy that behaves like Python < 3.12 (no is_junction)."""

    def __init__(self, entry):
        self._entry = entry

    def __getattr__(self, name):
        if name == "is_junction":
            raise AttributeError(name)
        return getattr(self._entry, name)


def scandir_without_is_junction(real_scandir):
    class Wrapper:
        def __init__(self, path):
            self._inner = real_scandir(path)

        def __enter__(self):
            return [HideIsJunction(entry) for entry in self._inner.__enter__()]

        def __exit__(self, *exc):
            return self._inner.__exit__(*exc)

    return Wrapper


class FailingEntry:
    """DirEntry proxy whose classification call raises OSError for one file name."""

    def __init__(self, entry, failing_method):
        self._entry = entry
        self._failing_method = failing_method

    def __getattr__(self, name):
        if name == self._failing_method and self._entry.name == "a.cs":
            def fail(*args, **kwargs):
                raise PermissionError(13, f"{name} denied")
            return fail
        return getattr(self._entry, name)


def scandir_with_failing_classification(real_scandir, failing_method):
    class Wrapper:
        def __init__(self, path):
            self._inner = real_scandir(path)

        def __enter__(self):
            return [FailingEntry(entry, failing_method) for entry in self._inner.__enter__()]

        def __exit__(self, *exc):
            return self._inner.__exit__(*exc)

    return Wrapper


class ArtifactDiffTests(unittest.TestCase):
    def setUp(self):
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.base = Path(self._temp.name).resolve()
        self.engine = self.base / "engine"
        self.sdk = self.base / "sdk"
        self.engine.mkdir()
        self.sdk.mkdir()

    def run_main(self, *extra):
        buffer = io.StringIO()
        code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), *extra], stdout=buffer)
        return code, json.loads(buffer.getvalue()) if buffer.getvalue() else None

    def paths(self, report, key):
        return [entry["path"] for entry in report[key]]

    def test_unequal_bytes_are_different_not_identical(self):
        write(self.engine, "web/a.cs", b"class A {}\n")
        write(self.sdk, "web/a.cs", b"class B {}\n")
        write(self.engine, "web/same.cs", b"same")
        write(self.sdk, "web/same.cs", b"same")
        report = compare_trees(self.engine, self.sdk)
        self.assertEqual(self.paths(report, "different"), ["web/a.cs"])
        self.assertEqual(self.paths(report, "identical"), ["web/same.cs"])
        entry = report["different"][0]
        self.assertEqual(entry["engineSha256"], hashlib.sha256(b"class A {}\n").hexdigest())
        self.assertEqual(entry["sdkSha256"], hashlib.sha256(b"class B {}\n").hexdigest())
        self.assertEqual((entry["engineSize"], entry["sdkSize"]), (11, 11))
        self.assertEqual(exit_code_for(report), artifact_diff.EXIT_DIFFERENCES)

    def test_equal_size_one_byte_difference_and_line_endings_are_not_normalised(self):
        write(self.engine, "x.txt", b"abc")
        write(self.sdk, "x.txt", b"abd")
        write(self.engine, "crlf.txt", b"a\r\nb\r\n")
        write(self.sdk, "crlf.txt", b"a\nb\n")
        write(self.engine, "eof.txt", b"a\n")
        write(self.sdk, "eof.txt", b"a")
        report = compare_trees(self.engine, self.sdk)
        self.assertEqual(self.paths(report, "different"), ["crlf.txt", "eof.txt", "x.txt"])
        self.assertEqual(report["summary"]["identical"], 0)

    def test_identical_tree_exits_zero_with_denominator(self):
        for name, data in (("a.cs", b"1"), ("sub/b.js", b"22")):
            write(self.engine, name, data)
            write(self.sdk, name, data)
        code, report = self.run_main()
        self.assertEqual(code, 0)
        self.assertEqual(report["summary"]["compared"], 2)
        self.assertEqual(report["summary"]["identical"], 2)
        self.assertTrue(report["summary"]["selectionComplete"])
        self.assertNotIn("identical", report)  # console output omits the identical list

    def test_missing_and_extra_files(self):
        write(self.engine, "both.cs", b"x")
        write(self.sdk, "both.cs", b"x")
        write(self.engine, "gen/only_engine.cs", b"e")
        write(self.sdk, "DeveloperMenu.xml", b"<m/>")
        code, report = self.run_main()
        self.assertEqual(code, 1)
        self.assertEqual(self.paths(report, "engineOnly"), ["gen/only_engine.cs"])
        self.assertEqual(self.paths(report, "sdkOnly"), ["DeveloperMenu.xml"])
        self.assertEqual(report["summary"]["compared"], 3)

    def test_empty_files_are_compared_and_distinguished_from_missing(self):
        write(self.engine, "empty.bin", b"")
        write(self.sdk, "empty.bin", b"")
        write(self.engine, "zero_vs_data.bin", b"")
        write(self.sdk, "zero_vs_data.bin", b"\0")
        report = compare_trees(self.engine, self.sdk)
        self.assertEqual(report["identical"][0]["size"], 0)
        self.assertEqual(report["identical"][0]["sha256"], hashlib.sha256(b"").hexdigest())
        self.assertEqual(self.paths(report, "different"), ["zero_vs_data.bin"])

    def test_both_trees_empty_is_an_error_not_a_pass(self):
        code, report = self.run_main()
        self.assertEqual(code, artifact_diff.EXIT_ERROR)
        self.assertEqual(report["summary"]["compared"], 0)

    def test_unicode_paths_and_deterministic_ordering(self):
        names = ["z.cs", "ação/Ünï.cs", "B.cs", "a.cs", "日本/語.cs", "a b.cs", "Z/9.cs"]
        for name in names:
            write(self.engine, name, name.encode("utf-8"))
        for name in reversed(names):
            write(self.sdk, name, name.encode("utf-8"))
        first = compare_trees(self.engine, self.sdk)
        second = compare_trees(self.engine, self.sdk)
        self.assertEqual(first, second)
        listed = self.paths(first, "identical")
        self.assertEqual(listed, sorted(names))
        self.assertIn("ação/Ünï.cs", listed)
        self.assertIn("日本/語.cs", listed)
        self.assertTrue(all("\\" not in path for path in listed))
        # JSON report survives a round trip with non-ASCII names.
        out = self.base / "report.json"
        code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(out)],
                    stdout=io.StringIO())
        self.assertEqual(code, 0)
        self.assertEqual(self.paths(json.loads(out.read_text(encoding="utf-8")), "identical"), listed)

    def test_unicode_normalisation_forms_are_not_merged(self):
        write(self.engine, "caf\u00e9.cs", b"x")
        write(self.sdk, "cafe\u0301.cs", b"x")
        report = compare_trees(self.engine, self.sdk)
        self.assertEqual(report["summary"]["identical"], 0)
        self.assertEqual(self.paths(report, "engineOnly"), ["caf\u00e9.cs"])
        self.assertEqual(self.paths(report, "sdkOnly"), ["cafe\u0301.cs"])

    def test_case_only_name_difference_is_classified_exactly_and_noted(self):
        write(self.engine, "DeveloperMenu.xml", b"x")
        write(self.sdk, "developermenu.xml", b"x")
        report = compare_trees(self.engine, self.sdk)
        self.assertEqual(report["summary"]["identical"], 0)
        self.assertEqual(report["caseOnlyNameMatches"],
                         [{"engine": "DeveloperMenu.xml", "sdk": "developermenu.xml"}])

    def test_extension_selection_is_explicit_and_reports_denominator(self):
        for root, cs_bytes in ((self.engine, b"e"), (self.sdk, b"s")):
            write(root, "a.CS", cs_bytes)
            write(root, "b.js", b"same")
            write(root, "c.bld", b"noise-" + cs_bytes)
        report = compare_trees(self.engine, self.sdk, [".cs", ".js"])
        self.assertEqual(report["extensions"], [".cs", ".js"])
        self.assertEqual(self.paths(report, "different"), ["a.CS"])
        self.assertEqual(self.paths(report, "identical"), ["b.js"])
        summary = report["summary"]
        self.assertEqual((summary["engineFilesScanned"], summary["engineFilesSelected"],
                          summary["engineExcludedByExtension"]), (3, 2, 1))
        self.assertEqual(summary["sdkExcludedByExtension"], 1)
        self.assertFalse(summary["selectionComplete"])
        # without selection the excluded file is compared and reported different
        self.assertEqual(len(compare_trees(self.engine, self.sdk)["different"]), 2)

    def test_bad_extension_rejected(self):
        for bad in ("cs", ".", "*.cs", ".a/b"):
            with self.subTest(bad=bad), self.assertRaises(ArtifactDiffError):
                compare_trees(self.engine, self.sdk, [bad])

    def test_missing_roots_rejected(self):
        with self.assertRaises(ArtifactDiffError):
            compare_trees(self.base / "nope", self.sdk)
        with self.assertRaises(ArtifactDiffError):
            compare_trees(self.engine, self.base / "nope")
        with self.assertRaises(ArtifactDiffError):
            compare_trees("", self.sdk)
        file_root = write(self.base, "file.txt", b"x")
        with self.assertRaises(ArtifactDiffError):
            compare_trees(file_root, self.sdk)

    def test_cli_bad_paths_exit_two_without_stdout(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--engine", str(self.base / "nope"), "--sdk", str(self.sdk)],
            capture_output=True, text=True)
        self.assertEqual(result.returncode, artifact_diff.EXIT_ERROR)
        self.assertEqual(result.stdout, "")
        self.assertIn("not an existing directory", result.stderr)

    def test_equal_nested_and_aliased_roots_rejected(self):
        write(self.engine, "inner/x.cs", b"x")
        (self.engine / "sdkinside").mkdir()
        for first, second in ((self.engine, self.engine), (self.engine, self.engine / "sdkinside"),
                              (self.engine / "inner", self.engine), (self.engine, str(self.engine) + os.sep)):
            with self.subTest(first=str(first), second=str(second)), self.assertRaises(ArtifactDiffError):
                compare_trees(first, second)

    def test_matching_extensions_do_not_claim_selection_complete_when_files_are_excluded(self):
        for root in (self.engine, self.sdk):
            write(root, "a.cs", b"same")
        write(self.sdk, "extra.xml", b"<x/>")
        code, report = self.run_main("--extension", ".cs")
        self.assertEqual(code, artifact_diff.EXIT_IDENTICAL)  # CLI status is unchanged
        self.assertFalse(report["summary"]["selectionComplete"])
        self.assertEqual(report["summary"]["sdkExcludedByExtension"], 1)
        self.assertFalse(compare_trees(self.engine, self.sdk, [".cs"])["summary"]["selectionComplete"])
        self.assertTrue(compare_trees(self.engine, self.sdk, [".cs", ".xml"])["summary"]["selectionComplete"])
        write(self.engine, "extra.xml", b"<x/>")
        self.assertTrue(compare_trees(self.engine, self.sdk)["summary"]["selectionComplete"])

    def test_empty_directories_are_not_reported(self):
        write(self.engine, "a.cs", b"same")
        write(self.sdk, "a.cs", b"same")
        (self.engine / "emptydir").mkdir()
        (self.sdk / "other" / "nested").mkdir(parents=True)
        report = compare_trees(self.engine, self.sdk)
        self.assertEqual(artifact_diff.exit_code_for(report), artifact_diff.EXIT_IDENTICAL)
        self.assertEqual(report["summary"]["compared"], 1)

    def test_symlink_or_junction_directory_entry_refused(self):
        outside = self.base / "outside"
        write(outside, "secret.cs", b"secret")
        make_directory_link(self, self.engine / "escape", outside)
        write(self.sdk, "a.cs", b"a")
        with self.assertRaisesRegex(ArtifactDiffError, "symlink or junction"):
            compare_trees(self.engine, self.sdk)

    @unittest.skipUnless(os.name == "nt", "NTFS junctions exist only on Windows")
    def test_windows_junction_entry_refused_in_each_tree(self):
        outside = self.base / "outside"
        write(outside, "secret.cs", b"secret")
        for root, other in ((self.engine, self.sdk), (self.sdk, self.engine)):
            with self.subTest(root=root.name):
                make_junction(root / "escape", outside)
                write(other, "a.cs", b"a")
                with self.assertRaisesRegex(ArtifactDiffError, "symlink or junction"):
                    compare_trees(self.engine, self.sdk)
                os.rmdir(root / "escape")  # removes the junction only
                self.assertTrue((outside / "secret.cs").is_file())

    @unittest.skipUnless(os.name == "nt", "NTFS junctions exist only on Windows")
    def test_windows_junction_refused_without_is_junction_support(self):
        outside = self.base / "outside"
        write(outside, "secret.cs", b"secret")
        make_junction(self.engine / "escape", outside)
        write(self.sdk, "a.cs", b"a")
        with mock.patch.object(artifact_diff.os, "scandir", scandir_without_is_junction(os.scandir)):
            with self.assertRaisesRegex(ArtifactDiffError, "symlink or junction"):
                compare_trees(self.engine, self.sdk)

    def test_reparse_attribute_is_refused_even_when_not_symlink_or_junction(self):
        class Entry:
            path = "x"

            def is_symlink(self):
                return False

            def stat(self, follow_symlinks=True):
                return mock.Mock(st_file_attributes=artifact_diff.FILE_ATTRIBUTE_REPARSE_POINT)

        self.assertTrue(artifact_diff._is_link(Entry()))
        plain = Entry()
        plain.stat = lambda follow_symlinks=True: mock.Mock(st_file_attributes=0x20)
        self.assertFalse(artifact_diff._is_link(plain))

    def test_symlinked_root_resolves_to_target_and_alias_of_other_root_is_rejected(self):
        write(self.engine, "a.cs", b"x")
        alias = self.base / "engine-alias"
        make_directory_link(self, alias, self.engine)
        with self.assertRaisesRegex(ArtifactDiffError, "equal or nested"):
            compare_trees(self.engine, alias)
        with self.assertRaisesRegex(ArtifactDiffError, "equal or nested"):
            compare_trees(alias, self.engine)
        write(self.sdk, "a.cs", b"x")
        report = compare_trees(alias, self.sdk)
        self.assertEqual(report["engineRoot"], os.path.realpath(self.engine))
        self.assertEqual(artifact_diff.exit_code_for(report), artifact_diff.EXIT_IDENTICAL)

    def test_unreadable_file_is_an_error_not_a_difference(self):
        write(self.engine, "a.cs", b"x")
        write(self.sdk, "a.cs", b"x")
        real_open = open

        def deny(path, *args, **kwargs):
            if str(path).endswith("a.cs"):
                raise PermissionError(13, "denied")
            return real_open(path, *args, **kwargs)

        with mock.patch.object(artifact_diff, "open", deny, create=True):
            with self.assertRaisesRegex(ArtifactDiffError, "cannot read"):
                compare_trees(self.engine, self.sdk)
            stderr = io.StringIO()
            with mock.patch.object(sys, "stderr", stderr):
                code = main(["--engine", str(self.engine), "--sdk", str(self.sdk)], stdout=io.StringIO())
        self.assertEqual(code, artifact_diff.EXIT_ERROR)
        self.assertIn("cannot read", stderr.getvalue())

    def test_unlistable_directory_is_an_error(self):
        write(self.engine, "sub/a.cs", b"x")
        write(self.sdk, "a.cs", b"x")
        real_scandir = os.scandir

        def deny(path):
            if str(path).endswith("sub"):
                raise PermissionError(13, "denied")
            return real_scandir(path)

        with mock.patch.object(artifact_diff.os, "scandir", deny):
            with self.assertRaisesRegex(ArtifactDiffError, "cannot list"):
                compare_trees(self.engine, self.sdk)

    def test_entry_classification_oserror_is_input_error_exit_two_not_differences(self):
        write(self.engine, "a.cs", b"engine-bytes")
        write(self.sdk, "a.cs", b"sdk-bytes")  # a successful scan would exit 1 (differences)
        self.assertEqual(self.run_main()[0], artifact_diff.EXIT_DIFFERENCES)
        for method in ("is_symlink", "is_dir", "is_file", "stat"):
            with self.subTest(method=method):
                wrapper = scandir_with_failing_classification(os.scandir, method)
                with mock.patch.object(artifact_diff.os, "scandir", wrapper):
                    with self.assertRaisesRegex(ArtifactDiffError, "cannot inspect"):
                        compare_trees(self.engine, self.sdk)
                    stdout, stderr = io.StringIO(), io.StringIO()
                    with mock.patch.object(sys, "stderr", stderr):
                        code = main(["--engine", str(self.engine), "--sdk", str(self.sdk)], stdout=stdout)
                self.assertEqual(code, artifact_diff.EXIT_ERROR)
                self.assertEqual(stdout.getvalue(), "")
                self.assertIn("cannot inspect", stderr.getvalue())

    def test_file_vanishing_between_listing_and_hashing_is_an_error(self):
        write(self.engine, "a.cs", b"x")
        write(self.sdk, "a.cs", b"x")
        real_list = artifact_diff._list_files

        def list_then_remove(root, max_files):
            files = real_list(root, max_files)
            if root == os.path.realpath(self.sdk) and "a.cs" in files:
                os.unlink(files["a.cs"])
            return files

        with mock.patch.object(artifact_diff, "_list_files", list_then_remove):
            with self.assertRaisesRegex(ArtifactDiffError, "cannot read"):
                compare_trees(self.engine, self.sdk)
            write(self.sdk, "a.cs", b"x")
            code = main(["--engine", str(self.engine), "--sdk", str(self.sdk)], stdout=io.StringIO())
        self.assertEqual(code, artifact_diff.EXIT_ERROR)

    def test_file_symlink_refused_when_creatable(self):
        target = write(self.base, "outside.cs", b"x")
        try:
            os.symlink(target, self.engine / "link.cs")
        except (OSError, NotImplementedError):
            self.skipTest("file symlink creation unavailable")
        with self.assertRaisesRegex(ArtifactDiffError, "symlink or junction"):
            compare_trees(self.engine, self.sdk)

    def test_out_inside_either_tree_refused_and_trees_never_modified(self):
        write(self.engine, "a.cs", b"1")
        write(self.sdk, "a.cs", b"2")
        before = (snapshot(self.engine), snapshot(self.sdk))
        for target in (self.engine / "report.json", self.sdk / "sub" / "report.json"):
            with self.subTest(target=str(target)):
                code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(target)],
                            stdout=io.StringIO())
                self.assertEqual(code, artifact_diff.EXIT_ERROR)
        code, _ = self.run_main()
        self.assertEqual(code, 1)
        outside = self.base / "report.json"
        main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(outside)],
             stdout=io.StringIO())
        self.assertTrue(outside.is_file())
        self.assertEqual((snapshot(self.engine), snapshot(self.sdk)), before)

    def test_out_leaf_hardlink_to_either_tree_is_replaced_not_written_through(self):
        for label, root in ("engine", self.engine), ("sdk", self.sdk):
            with self.subTest(tree=label):
                write(self.engine, "a.cs", b"engine-bytes")
                write(self.sdk, "a.cs", b"sdk-bytes")
                before = (snapshot(self.engine), snapshot(self.sdk))
                out = self.base / f"hard-{label}.json"
                try:
                    os.link(root / "a.cs", out)
                except OSError:
                    self.skipTest("hardlink creation unavailable")
                code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(out)],
                            stdout=io.StringIO())
                self.assertEqual(code, artifact_diff.EXIT_DIFFERENCES)
                self.assertEqual((snapshot(self.engine), snapshot(self.sdk)), before)
                self.assertEqual(json.loads(out.read_text(encoding="utf-8"))["summary"]["different"], 1)
                self.assertEqual(os.stat(root / "a.cs").st_nlink, 1)
                self.assertEqual([p.name for p in self.base.iterdir() if p.name.endswith(".tmp")], [])

    def test_out_leaf_file_symlink_to_either_tree_is_replaced_not_written_through(self):
        for label, root in ("engine", self.engine), ("sdk", self.sdk):
            with self.subTest(tree=label):
                write(self.engine, "a.cs", b"engine-bytes")
                write(self.sdk, "a.cs", b"sdk-bytes")
                before = (snapshot(self.engine), snapshot(self.sdk))
                out = self.base / f"sym-{label}.json"
                try:
                    os.symlink(root / "a.cs", out)
                except (OSError, NotImplementedError):
                    self.skipTest("file symlink creation unavailable")
                code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(out)],
                            stdout=io.StringIO())
                self.assertEqual(code, artifact_diff.EXIT_DIFFERENCES)
                self.assertEqual((snapshot(self.engine), snapshot(self.sdk)), before)
                self.assertFalse(out.is_symlink())
                self.assertEqual(json.loads(out.read_text(encoding="utf-8"))["summary"]["different"], 1)

    def test_out_leaf_directory_link_never_writes_through_or_alters_inputs(self):
        write(self.engine, "a.cs", b"engine-bytes")
        write(self.sdk, "a.cs", b"sdk-bytes")
        for label, root in ("engine", self.engine), ("sdk", self.sdk):
            with self.subTest(tree=label):
                before = (snapshot(self.engine), snapshot(self.sdk))
                out = self.base / f"dirlink-{label}"
                make_directory_link(self, out, root)
                code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(out)],
                            stdout=io.StringIO())
                self.assertIn(code, (artifact_diff.EXIT_DIFFERENCES, artifact_diff.EXIT_ERROR))
                self.assertEqual((snapshot(self.engine), snapshot(self.sdk)), before)
                self.assertEqual([p.name for p in self.base.iterdir() if p.name.endswith(".tmp")], [])
                if os.path.isdir(out):  # a refused replace leaves the link, never a report inside it
                    self.assertEqual(code, artifact_diff.EXIT_ERROR)
                    os.rmdir(out)  # removes the link only
                else:
                    self.assertEqual(json.loads(out.read_text(encoding="utf-8"))["summary"]["different"], 1)
                    out.unlink()

    def test_out_through_parent_alias_of_either_tree_is_refused_without_overwriting(self):
        write(self.engine, "a.cs", b"engine-bytes")
        write(self.sdk, "a.cs", b"sdk-bytes")
        before = (snapshot(self.engine), snapshot(self.sdk))
        for label, root in ("engine", self.engine), ("sdk", self.sdk):
            with self.subTest(tree=label):
                alias = self.base / f"alias-{label}"
                make_directory_link(self, alias, root)
                code = main(["--engine", str(self.engine), "--sdk", str(self.sdk),
                             "--out", str(alias / "a.cs")], stdout=io.StringIO())
                self.assertEqual(code, artifact_diff.EXIT_ERROR)
                self.assertEqual((snapshot(self.engine), snapshot(self.sdk)), before)

    def test_out_failure_exits_two_and_leaves_no_temporary_file(self):
        write(self.engine, "a.cs", b"1")
        write(self.sdk, "a.cs", b"1")
        out = self.base / "report-is-a-directory"
        out.mkdir()
        code = main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--out", str(out)],
                    stdout=io.StringIO())
        self.assertEqual(code, artifact_diff.EXIT_ERROR)
        self.assertEqual([p.name for p in self.base.iterdir() if p.name.endswith(".tmp")], [])

    def test_max_files_bound(self):
        for index in range(3):
            write(self.engine, f"f{index}.cs", b"x")
            write(self.sdk, f"f{index}.cs", b"x")
        with self.assertRaisesRegex(ArtifactDiffError, "more than 2 files"):
            compare_trees(self.engine, self.sdk, max_files=2)
        self.assertEqual(main(["--engine", str(self.engine), "--sdk", str(self.sdk), "--max-files", "0"],
                              stdout=io.StringIO()), artifact_diff.EXIT_ERROR)

    def test_subprocess_exit_codes(self):
        write(self.engine, "a.cs", b"same")
        write(self.sdk, "a.cs", b"same")
        base = [sys.executable, str(SCRIPT), "--engine", str(self.engine), "--sdk", str(self.sdk)]
        self.assertEqual(subprocess.run(base, capture_output=True).returncode, 0)
        write(self.sdk, "extra.cs", b"x")
        self.assertEqual(subprocess.run(base, capture_output=True).returncode, 1)


if __name__ == "__main__":
    unittest.main()
