#!/usr/bin/env python3
"""Generate and validate the published MCP operation inventory.

The executable policy remains in OperationClassifier.cs.  This command projects
that policy together with tool_definitions.json into a reviewable JSON artifact,
then fails closed when a published action is missing from the classifier.  It is
deliberately source based: no gateway process or KB is required to run it.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Iterable


ROOT = Path(__file__).resolve().parents[1]
CLASSIFIER = ROOT / "src" / "GxMcp.Gateway" / "OperationClassifier.cs"
TOOLS = ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json"
OUTPUT = ROOT / "docs" / "operation-contract-inventory.json"
REGISTRY = ROOT / "src" / "GxMcp.Gateway" / "EngineCapabilityRegistry.cs"
ENGINE_DOC = ROOT / "docs" / "engine_backend.md"


def _strings(value: str) -> list[str]:
    return re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', value)


# A declaration is recognised by its field name in the leading token run of a
# line, followed by `=`.  Modifier text, attributes, line wrapping and indentation
# are deliberately not part of the match.
_DECLARATION = r"(?m)^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:[\w\[\]<>,\.]+[ \t\r\n]+)*?{name}[ \t\r\n]*=(?!=)"


def _skip_trivia(source: str, index: int) -> int:
    """Return the index just past the literal or comment starting at ``index``.

    Brace matching is only sound when a brace inside a string, char literal or
    comment is never counted, so each of those is consumed as an opaque unit.
    Returns ``index`` unchanged when no literal or comment starts there.
    """
    if source.startswith("//", index):
        newline = source.find("\n", index)
        return len(source) if newline < 0 else newline
    if source.startswith("/*", index):
        closing = source.find("*/", index + 2)
        return len(source) if closing < 0 else closing + 2
    if source[index] == "@" or (source[index] == "$" and source.startswith('@"', index + 1)):
        cursor = index + 1 if source[index] == "@" else index + 2
        while cursor < len(source):
            if source[cursor] == '"':
                if source.startswith('""', cursor):
                    cursor += 2
                    continue
                return cursor + 1
            cursor += 1
        return cursor
    if source.startswith('"""', index) or source[index] in "\"'":
        quote = '"""' if source.startswith('"""', index) else source[index]
        cursor = index + len(quote)
        while cursor < len(source):
            if source.startswith(quote, cursor):
                return cursor + len(quote)
            if quote != '"""' and source[cursor] == "\\":
                cursor += 2
                continue
            cursor += 1
        return cursor
    return index


def _balanced(source: str, cursor: int, open_char: str, close_char: str) -> tuple[int, int] | None:
    """Return the indexes of the first ``open_char`` at or after ``cursor`` and its balancing ``close_char``.

    Literals and comments are skipped as opaque units.  ``None`` when unterminated.
    """
    depth = 0
    opened = -1
    while cursor < len(source):
        skipped = _skip_trivia(source, cursor)
        if skipped != cursor:
            cursor = skipped
            continue
        character = source[cursor]
        if character == open_char:
            opened = cursor if opened < 0 else opened
            depth += 1
        elif character == close_char and opened >= 0:
            depth -= 1
            if depth == 0:
                return opened, cursor
        cursor += 1
    return None


def _initialiser(source: str, field: str, file_name: str = "OperationClassifier.cs") -> str:
    """Return the text inside ``field``'s collection initialiser.

    Tolerant of the declaration's modifier text and indentation, and of nested
    braces in the body: the end is the brace that balances the initialiser's
    opening brace rather than an expected ``\\n        };``.  Fails closed, so a
    renamed or deleted declaration raises instead of reading an empty set.
    """
    match = re.search(_DECLARATION.format(name=re.escape(field)), source)
    if not match:
        raise ValueError(f"could not locate {field} in {file_name}")
    span = _balanced(source, match.end(), "{", "}")
    if span is None:
        raise ValueError(f"unterminated initialiser for {field} in {file_name}")
    return source[span[0] + 1 : span[1]]


def _set_block(source: str, field: str) -> set[str]:
    return set(_strings(_initialiser(source, field)))


def read_policy(path: Path) -> tuple[dict[str, dict[str, set[str]]], dict[str, set[str]], set[str]]:
    source = path.read_text(encoding="utf-8")
    contracts: dict[str, dict[str, set[str]]] = {}
    # Bounded by brace matching, not by the comment that happens to follow the
    # dictionary today.
    block = _initialiser(source, "ActionContracts")
    entries = list(re.finditer(r'\[\s*"([^"]+)"\s*\]\s*=\s*Contract\s*\(', block))
    if not entries:
        raise ValueError("could not locate any ActionContracts entry in OperationClassifier.cs")
    for index, entry in enumerate(entries):
        segment_end = entries[index + 1].start() if index + 1 < len(entries) else len(block)
        segment = block[entry.end() : segment_end]
        read_marker = segment.find("readOnly:")
        mutate_marker = segment.find("mutating:")
        if read_marker < 0 or mutate_marker < 0 or mutate_marker <= read_marker:
            raise ValueError(f"malformed contract for {entry.group(1)}")
        contracts[entry.group(1)] = {
            "readOnly": set(_strings(segment[read_marker + len("readOnly:") : mutate_marker])),
            "mutating": set(_strings(segment[mutate_marker + len("mutating:") :])),
        }

    named = {
        field: _set_block(source, field)
        for field in (
            "PureReadOnlyTools",
            "KnownMutatingTools",
            "ModeDependentTools",
            "NameOnlyMutatingTools",
            "DryRunCapableActions",
        )
    }
    return contracts, named, named["DryRunCapableActions"]


def classify(tool: str, action: str | None, contracts: dict[str, dict[str, set[str]]], named: dict[str, set[str]]) -> str:
    if action and tool in contracts:
        if action in contracts[tool]["readOnly"]:
            return "readOnly"
        if action in contracts[tool]["mutating"]:
            return "mutating"
        return "unknown"
    if tool in named["PureReadOnlyTools"]:
        return "readOnly"
    if tool in named["KnownMutatingTools"] or tool in named["NameOnlyMutatingTools"]:
        return "mutating"
    if tool in named["ModeDependentTools"]:
        return "modeDependent"
    return "unknown"


def router_for(tool: str) -> str:
    if tool == "genexus_lifecycle" or tool.startswith("genexus_kb") or tool in {
        "genexus_whoami", "genexus_doctor", "genexus_connection_recover",
        "genexus_worker_reload", "genexus_navigation", "genexus_build_plan",
        "genexus_validate", "genexus_build", "genexus_test", "genexus_doc",
    }:
        return "SystemRouter"
    if tool in {"genexus_read", "genexus_inspect", "genexus_analyze", "genexus_search_source", "genexus_linter", "genexus_get_navigation", "genexus_get_signature", "genexus_inject_context"}:
        return "AnalyzeRouter"
    return "OperationsRouter"


def effect_for(kind: str, tool: str) -> str:
    if kind == "readOnly":
        return "kb.read"
    if tool in {"genexus_connection_recover", "genexus_worker_reload"}:
        return "process.write"
    if tool in {"genexus_test", "genexus_run_object"}:
        return "external.execute"
    if tool == "genexus_sdk_probe":
        return "file.write"
    if kind == "mutating":
        return "kb.write"
    return "unknown"


def operation_fields(tool: str, action: str | None, kind: str, preview_actions: set[str]) -> dict:
    preview_supported = bool(action and f"{tool}:{action}" in preview_actions)
    if tool == "genexus_connection_recover" and action == "journal_status":
        return {
            "kind": "readOnly", "effects": "file.read", "execution": "gateway",
            "retry": "safe", "cache": "never", "invalidation": [], "previewSupported": False,
        }
    if tool == "genexus_connection_recover" and action == "recovery_read_complete":
        # Reads one part through the Worker (nothing is written to the KB) and, only
        # when dryRun is explicitly false, rewrites the recovery journal.
        preview = {
            "kind": "readOnly", "effects": "kb.read", "execution": "gateway",
            "retry": "safe", "cache": "never", "invalidation": [], "previewSupported": True,
        }
        applied = {
            "kind": "mutating", "effects": "file.write", "execution": "gateway",
            "retry": "operation_key", "cache": "never", "invalidation": ["files"],
            "previewSupported": True,
        }
        return {
            **preview,
            "variants": [
                {"selector": {"dryRun": "not false"}, **preview},
                {"selector": {"dryRun": False}, **applied},
            ],
        }
    if tool == "genexus_connection_recover" and action == "journal_repair":
        preview = {
            "kind": "readOnly", "effects": "file.read", "execution": "gateway",
            "retry": "safe", "cache": "never", "invalidation": [], "previewSupported": True,
        }
        applied = {
            "kind": "mutating", "effects": "file.write", "execution": "gateway",
            "retry": "operation_key", "cache": "never", "invalidation": ["files"],
            "previewSupported": True,
        }
        return {
            **preview,
            "variants": [
                {"selector": {"dryRun": "not false"}, **preview},
                {"selector": {"dryRun": False}, **applied},
            ],
        }
    if tool in {"genexus_connection_recover", "genexus_worker_reload"}:
        return {
            "kind": kind, "effects": "process.write", "execution": "gateway",
            "retry": "operation_key", "cache": "never", "invalidation": ["process", "sessions"],
            "previewSupported": preview_supported,
        }
    # A module install cannot be repeated blindly: the SDK import is not
    # transactional, so a retry has to reconcile the resulting inventory instead
    # of assuming nothing happened. Classified here so the per-action and
    # tool-level paths agree.
    if tool == "genexus_module" and action in {"install", "install_builtin"}:
        retry = "reconcile_inventory"
    else:
        retry = "safe" if kind == "readOnly" else ("operation_key" if kind == "mutating" else "never")
    return {
        "kind": kind,
        "effects": effect_for(kind, tool),
        "execution": "worker" if kind in {"readOnly", "mutating"} else "unknown",
        "retry": retry,
        "cache": "semantic" if kind == "readOnly" else "never",
        "invalidation": [] if kind != "mutating" else ["kb", "dependents", "collections"],
        "previewSupported": preview_supported,
    }


def build_inventory(tools_path: Path = TOOLS, classifier_path: Path = CLASSIFIER) -> dict:
    tools = json.loads(tools_path.read_text(encoding="utf-8"))
    contracts, named, preview_actions = read_policy(classifier_path)
    entries: list[dict] = []
    errors: list[str] = []
    for definition in tools:
        tool = definition.get("name")
        if not isinstance(tool, str) or not tool:
            errors.append("tool without a name")
            continue
        props = definition.get("inputSchema", {}).get("properties", {})
        action_schema = props.get("action", {}) if isinstance(props, dict) else {}
        actions = action_schema.get("enum", []) if isinstance(action_schema, dict) else []
        actions = [str(action) for action in actions]
        rows = []
        if actions:
            for action in actions:
                kind = classify(tool, action, contracts, named)
                if kind == "unknown":
                    errors.append(f"{tool}:{action}")
                rows.append({"action": action, **operation_fields(tool, action, kind, preview_actions)})
        else:
            kind = classify(tool, None, contracts, named)
            if kind == "unknown":
                errors.append(tool)
            rows.append({"action": None, **operation_fields(tool, None, kind, preview_actions)})
        entries.append({
            "tool": tool,
            "router": router_for(tool),
            "actions": rows,
            "inputExamples": len(action_schema.get("examples", [])) if isinstance(action_schema, dict) else 0,
            "outputSchema": "outputSchema" in definition,
        })
    if errors:
        raise ValueError("unclassified published operations: " + ", ".join(sorted(errors)))
    return {
        # Keep v1: `variants` is an additive field and each action retains its
        # original top-level selector-default contract for existing readers.
        "schemaVersion": "genexus-operation-inventory/1",
        "source": [str(tools_path.relative_to(ROOT)).replace("\\", "/"), str(classifier_path.relative_to(ROOT)).replace("\\", "/")],
        "toolCount": len(entries),
        "actionCount": sum(len(item["actions"]) for item in entries),
        "tools": entries,
    }


def _top_level_arguments(text: str) -> list[str]:
    """Split a call's argument text on the commas outside nested brackets, strings and comments."""
    parts: list[str] = []
    depth = 0
    start = 0
    cursor = 0
    while cursor < len(text):
        skipped = _skip_trivia(text, cursor)
        if skipped != cursor:
            cursor = skipped
            continue
        character = text[cursor]
        if character in "([{":
            depth += 1
        elif character in ")]}":
            depth -= 1
        elif character == "," and depth == 0:
            parts.append(text[start:cursor])
            start = cursor + 1
        cursor += 1
    parts.append(text[start:])
    return [part.strip() for part in parts if part.strip()]


def read_native_routed_operations(path: Path = REGISTRY) -> list[str]:
    """The product operations (``tool`` or ``tool.action``) the engine may serve.

    Mirrors ``EngineCapabilityRegistry.RoutedOperations``: the consumers of every capability
    whose status lets it route.  Fails closed if that routing rule is no longer the one this
    projection assumes, or if the registry cannot be read.
    """
    source = path.read_text(encoding="utf-8")
    if not re.search(r"MayRouteToEngine\s*=>\s*Status\s*==\s*EngineCapabilityStatus\.Parity\s*;", source):
        raise ValueError(f"{path.name}: MayRouteToEngine is no longer 'Status == Parity'; the routed-operation projection must be revisited")
    body = _initialiser(source, "All", path.name)
    operations: list[str] = []
    capabilities = 0
    for start in re.finditer(r"new\s+EngineCapability\s*\(", body):
        span = _balanced(body, start.end() - 1, "(", ")")
        if span is None:
            raise ValueError(f"{path.name}: unterminated EngineCapability declaration")
        arguments = {}
        for argument in _top_level_arguments(body[span[0] + 1 : span[1]]):
            name, separator, value = argument.partition(":")
            if separator:
                arguments[name.strip()] = value.strip()
        status = re.fullmatch(r"EngineCapabilityStatus\.(\w+)", arguments.get("status", ""))
        consumers = arguments.get("consumers", "")
        if not status or not (consumers == "Array.Empty<string>()" or consumers.startswith("new[]")):
            raise ValueError(f"{path.name}: an EngineCapability has no readable status or consumers")
        capabilities += 1
        if status.group(1) == "Parity":
            for consumer in _strings(consumers):
                if consumer not in operations:
                    operations.append(consumer)
    if capabilities == 0:
        raise ValueError(f"{path.name}: no EngineCapability declarations found")
    return operations


def native_coverage(inventory: dict, operations: list[str]) -> tuple[list[str], int]:
    """The distinct tools and the inventory operation rows that ``operations`` name.

    Every operation must resolve to exactly one published row: ``tool.action`` to that action, a
    bare ``tool`` to the row of a tool that has no action selector.
    """
    rows_by_tool = {entry["tool"]: [row["action"] for row in entry["actions"]] for entry in inventory["tools"]}
    tools: list[str] = []
    for operation in operations:
        tool, _, action = operation.partition(".")
        if tool not in rows_by_tool:
            raise ValueError(f"routed operation {operation} names a tool that is not published")
        if (action or None) not in rows_by_tool[tool]:
            raise ValueError(f"routed operation {operation} names no published operation row")
        if tool not in tools:
            tools.append(tool)
    return tools, len(operations)


def check_engine_doc(inventory: dict, operations: list[str], doc_path: Path = ENGINE_DOC) -> None:
    """Fail when the hand-written coverage accounting disagrees with the registry and inventory."""
    tools, rows = native_coverage(inventory, operations)
    document = doc_path.read_text(encoding="utf-8")
    heading = document.find("## Coverage accounting")
    if heading < 0:
        raise ValueError(f"{doc_path.name}: no '## Coverage accounting' section")
    end = document.find("\n## ", heading + 1)
    section = document[heading : end if end >= 0 else len(document)]
    total_tools, total_rows = inventory["toolCount"], inventory["actionCount"]
    expected = {
        r"inventory\s+contains\s+\*\*(\d+)\s+tools\s+and\s+(\d+)\s+operation\s+rows\*\*": (str(total_tools), str(total_rows)),
        r"admits\s+a\s+restricted\s+subset\s+of\s+\*\*(\d+)\s+tools\s+\((\d+)%\)\*\*": (
            str(len(tools)), f"{100 * len(tools) / total_tools:.0f}"),
        r"Those\s+are\s+\*\*(\d+)\s+of\s+(\d+)\s+operation\s+rows\s+\((\d+(?:\.\d+)?)%\)\*\*": (
            str(rows), str(total_rows), f"{100 * rows / total_rows:.1f}"),
    }
    for pattern, values in expected.items():
        match = re.search(pattern, section)
        if not match:
            raise ValueError(f"{doc_path.name}: coverage sentence not found: {pattern}")
        if match.groups() != values:
            raise ValueError(
                f"{doc_path.name}: coverage says {match.group(0)!r}; the registry and inventory give {values}")
    certified = re.search(r"\*\*\d+\s+of\s+(\d+)\s+tools\s+are\s+certified", document)
    if not certified or certified.group(1) != str(total_tools):
        raise ValueError(f"{doc_path.name}: 'tools are certified' denominator is not the published tool count {total_tools}")
    missing = [tool for tool in tools if f"`{tool}`" not in section]
    if missing:
        raise ValueError(f"{doc_path.name}: coverage section does not name routed tools: {', '.join(missing)}")


def verify_classifier_policy() -> None:
    """Cross-check the issue-scoped variants against OperationClassifier.Describe."""
    project = ROOT / "src" / "GxMcp.Gateway.Tests" / "GxMcp.Gateway.Tests.csproj"
    command = [
        "dotnet", "test", str(project), "--no-restore",
        "--filter", "FullyQualifiedName~OperationInventoryClassifierParityTests",
        "--logger", "console;verbosity=minimal",
    ]
    try:
        result = subprocess.run(
            command, cwd=ROOT, capture_output=True, text=True, check=False, timeout=60
        )
    except subprocess.TimeoutExpired as exc:
        raise ValueError("OperationClassifier parity test timed out after 60 seconds") from exc
    if result.returncode != 0:
        output = (result.stdout or "") + (result.stderr or "")
        raise ValueError("OperationClassifier parity test failed:\n" + output[-4000:])


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="write the deterministic inventory JSON")
    parser.add_argument("--check", action="store_true", help="compare the generated inventory with the published artifact")
    args = parser.parse_args(list(argv) if argv is not None else None)
    try:
        inventory = build_inventory()
        output_path = args.output or OUTPUT
        if args.check:
            published = json.loads(output_path.read_text(encoding="utf-8"))
            if published != inventory:
                raise ValueError(f"published inventory differs from generated policy: {output_path}")
            check_engine_doc(inventory, read_native_routed_operations(REGISTRY), ENGINE_DOC)
            # The Python projection has issue-scoped special variants; validate those
            # against the executable C# policy too. A generic C#-driven projection
            # remains a separate follow-up rather than silently widening this gate.
            verify_classifier_policy()
        if args.output and not args.check:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(inventory, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        native_tools, native_rows = native_coverage(inventory, read_native_routed_operations(REGISTRY))
        print(
            f"operation-inventory: valid tools={inventory['toolCount']} actions={inventory['actionCount']} "
            f"native-routed tools={len(native_tools)} rows={native_rows}")
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"operation-inventory: invalid: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
