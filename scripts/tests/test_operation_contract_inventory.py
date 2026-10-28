import contextlib
import io
import json
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "generate-operation-contract-inventory.py"
INVENTORY = ROOT / "docs" / "operation-contract-inventory.json"
POLICY_FIELDS = (
    "PureReadOnlyTools",
    "KnownMutatingTools",
    "ModeDependentTools",
    "NameOnlyMutatingTools",
    "DryRunCapableActions",
    "ActionContracts",
)


def _load_generator():
    """Load the generator script the way the rest of this file already does."""
    from importlib.util import module_from_spec, spec_from_file_location
    spec = spec_from_file_location("inventory", SCRIPT)
    module = module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


def _drop_declaration(source: str, field: str) -> str:
    """Return *source* with ``field``'s declaration and body removed."""
    lines = source.splitlines(keepends=True)
    start = next(index for index, line in enumerate(lines) if field in line)
    end = next(index for index in range(start, len(lines)) if lines[index].strip() == "};")
    del lines[start : end + 1]
    return "".join(lines)


class OperationContractInventoryTests(unittest.TestCase):
    def test_committed_inventory_is_generated_and_complete(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--check"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        self.assertEqual(inventory["schemaVersion"], "genexus-operation-inventory/1")
        self.assertEqual(inventory["toolCount"], 60)
        self.assertGreaterEqual(inventory["actionCount"], 247)
        self.assertTrue(all(row["actions"] for row in inventory["tools"]))
        for tool in inventory["tools"]:
            for action in tool["actions"]:
                self.assertIn(action["kind"], {"readOnly", "mutating", "modeDependent"})
                self.assertIn(action["retry"], {"safe", "operation_key", "never", "reconcile_inventory"})
                self.assertIn(action["cache"], {"semantic", "never"})

    def test_generator_detects_new_unclassified_action(self):
        definitions = json.loads((ROOT / "src/GxMcp.Gateway/tool_definitions.json").read_text(encoding="utf-8"))
        for tool in definitions:
            if tool.get("name") == "genexus_lifecycle":
                tool["inputSchema"]["properties"]["action"]["enum"].append("__inventory_gap__")
                break
        with tempfile.TemporaryDirectory() as temp:
            temp_path = Path(temp) / "tools.json"
            temp_path.write_text(json.dumps(definitions), encoding="utf-8")
            # The source-level command accepts the production path by design;
            # this focused check exercises the same fail-closed policy directly.
            from importlib.util import module_from_spec, spec_from_file_location
            spec = spec_from_file_location("inventory", SCRIPT)
            module = module_from_spec(spec)
            assert spec and spec.loader
            spec.loader.exec_module(module)
            with self.assertRaises(ValueError):
                module.build_inventory(temp_path, module.CLASSIFIER)

    def test_mode_dependent_analyze_is_registered_in_inventory(self):
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        analyze = next(row for row in inventory["tools"] if row["tool"] == "genexus_analyze")
        self.assertEqual(analyze["actions"][0]["kind"], "modeDependent")

    def test_journal_policy_includes_argument_selected_variants(self):
        from importlib.util import module_from_spec, spec_from_file_location
        spec = spec_from_file_location("inventory", SCRIPT)
        module = module_from_spec(spec)
        assert spec and spec.loader
        spec.loader.exec_module(module)
        inventory = module.build_inventory()
        tools = {row["tool"]: row for row in inventory["tools"]}
        recovery = {row["action"]: row for row in tools["genexus_connection_recover"]["actions"]}

        status = recovery["journal_status"]
        self.assertEqual(status["kind"], "readOnly")
        self.assertEqual(status["effects"], "file.read")
        self.assertEqual(status["execution"], "gateway")
        self.assertEqual(status["retry"], "safe")
        self.assertEqual(status["cache"], "never")
        self.assertEqual(status["invalidation"], [])
        self.assertFalse(status["previewSupported"])

        repair = recovery["journal_repair"]
        self.assertEqual(repair["kind"], "readOnly")
        self.assertEqual(repair["effects"], "file.read")
        self.assertTrue(repair["previewSupported"])
        self.assertEqual([variant["selector"] for variant in repair["variants"]],
                         [{"dryRun": "not false"}, {"dryRun": False}])
        self.assertEqual(repair["variants"][0]["kind"], "readOnly")
        self.assertEqual(repair["variants"][0]["retry"], "safe")
        self.assertEqual(repair["variants"][1]["kind"], "mutating")
        self.assertEqual(repair["variants"][1]["effects"], "file.write")
        self.assertEqual(repair["variants"][1]["invalidation"], ["files"])

        # Reads one part through the Worker and rewrites only the recovery journal when
        # dryRun is explicitly false: never a KB write, a process write or an execution.
        certified = recovery["recovery_read_complete"]
        self.assertEqual(certified["kind"], "readOnly")
        self.assertEqual(certified["effects"], "kb.read")
        self.assertTrue(certified["previewSupported"])
        self.assertEqual([variant["selector"] for variant in certified["variants"]],
                         [{"dryRun": "not false"}, {"dryRun": False}])
        self.assertEqual(certified["variants"][0]["kind"], "readOnly")
        self.assertEqual(certified["variants"][0]["retry"], "safe")
        self.assertEqual(certified["variants"][1]["kind"], "mutating")
        self.assertEqual(certified["variants"][1]["effects"], "file.write")
        self.assertEqual(certified["variants"][1]["retry"], "operation_key")
        self.assertEqual(certified["variants"][1]["invalidation"], ["files"])

        for tool_name in ("genexus_connection_recover", "genexus_worker_reload"):
            action = tools[tool_name]["actions"][0]
            self.assertEqual(action["execution"], "gateway")
            self.assertEqual(action["effects"], "process.write")
            self.assertEqual(action["invalidation"], ["process", "sessions"])

    def test_check_detects_stale_published_inventory(self):
        with tempfile.TemporaryDirectory() as temp:
            stale = Path(temp) / "operation-contract-inventory.json"
            stale.write_text("{}\n", encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--check", "--output", str(stale)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
        self.assertEqual(result.returncode, 2)
        self.assertIn("published inventory differs", result.stderr)

    def test_module_installations_require_inventory_reconciliation(self):
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        module = next(row for row in inventory["tools"] if row["tool"] == "genexus_module")
        actions = {row["action"]: row for row in module["actions"]}
        for name in ("install", "install_builtin"):
            self.assertEqual(actions[name]["retry"], "reconcile_inventory")
            self.assertEqual(actions[name]["kind"], "mutating")
        self.assertEqual(actions["list"]["retry"], "safe")

    def test_read_policy_locates_every_required_field(self):
        module = _load_generator()
        contracts, named, preview_actions = module.read_policy(module.CLASSIFIER)
        self.assertTrue(contracts)
        for field in POLICY_FIELDS:
            with self.subTest(field=field):
                if field == "ActionContracts":
                    self.assertIn("genexus_module", contracts)
                else:
                    self.assertIn(field, named)
                    self.assertTrue(named[field])
        self.assertEqual(preview_actions, named["DryRunCapableActions"])

    def test_read_policy_is_insensitive_to_reformatting(self):
        module = _load_generator()
        expected = module.read_policy(module.CLASSIFIER)
        source = module.CLASSIFIER.read_text(encoding="utf-8")
        indented = "".join(
            "    " + line if line.strip() else line for line in source.splitlines(keepends=True)
        )
        stripped = re.sub(r"(?m)^([ \t]*)private ", r"\1", source)
        restyled = re.sub(r"(?m)^([ \t]*)private ", r"\1", indented)
        with tempfile.TemporaryDirectory() as temp:
            for label, text in (
                ("indented", indented),
                ("private stripped", stripped),
                ("both", restyled),
            ):
                with self.subTest(reformatting=label):
                    copy = Path(temp) / "OperationClassifier.cs"
                    copy.write_text(text, encoding="utf-8")
                    self.assertEqual(module.read_policy(copy), expected)

    def test_read_policy_still_fails_closed_on_a_missing_field(self):
        module = _load_generator()
        source = module.CLASSIFIER.read_text(encoding="utf-8")
        with tempfile.TemporaryDirectory() as temp:
            for field in POLICY_FIELDS:
                with self.subTest(field=field):
                    copy = Path(temp) / "OperationClassifier.cs"
                    copy.write_text(_drop_declaration(source, field), encoding="utf-8")
                    with self.assertRaises(ValueError) as caught:
                        module.read_policy(copy)
                    self.assertIn(field, str(caught.exception))


def _bump_first_number(text: str, pattern: str, group: int) -> str:
    """Return *text* with capture *group* of the first *pattern* match incremented by one."""
    match = re.search(pattern, text)
    assert match, pattern
    start, end = match.span(group)
    return text[:start] + str(int(match.group(group)) + 1) + text[end:]


class NativeRoutingCoverageTests(unittest.TestCase):
    """The doc's natively routed counts are derived from the capability registry, never hand-kept."""

    COVERAGE_SENTENCES = (
        (r"contains \*\*(\d+) tools and (\d+) operation rows", 1),
        (r"contains \*\*(\d+) tools and (\d+) operation rows", 2),
        (r"subset of \*\*(\d+) tools \((\d+)%\)", 1),
        (r"subset of \*\*(\d+) tools \((\d+)%\)", 2),
        (r"Those are \*\*(\d+) of (\d+) operation rows \((\d+)\.\d%\)", 1),
        (r"Those are \*\*(\d+) of (\d+) operation rows \((\d+)\.\d%\)", 2),
    )

    def setUp(self):
        self.module = _load_generator()
        self.inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        self.registry = self.module.REGISTRY.read_text(encoding="utf-8")
        self.doc = self.module.ENGINE_DOC.read_text(encoding="utf-8")
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)

    def _registry_with(self, text: str) -> Path:
        path = Path(self.temp.name) / "EngineCapabilityRegistry.cs"
        path.write_text(text, encoding="utf-8")
        return path

    def _doc_with(self, text: str) -> Path:
        path = Path(self.temp.name) / "engine_backend.md"
        path.write_text(text, encoding="utf-8")
        return path

    def test_committed_doc_matches_the_registry_and_inventory(self):
        operations = self.module.read_native_routed_operations(self.module.REGISTRY)
        self.assertTrue(operations)
        self.module.check_engine_doc(self.inventory, operations, self.module.ENGINE_DOC)

    def test_only_parity_consumers_are_routed(self):
        operations = self.module.read_native_routed_operations(self.module.REGISTRY)
        self.assertNotIn("genexus_read", operations)
        self.assertNotIn("genexus_edit", operations)
        # A Partial capability that names a consumer adds nothing, even when its note quotes Parity syntax.
        start = self.registry.index('id: "numeric_core"')
        head, tail = self.registry[:start], self.registry[start:]
        tail = tail.replace("consumers: Array.Empty<string>(),", 'consumers: new[] { "genexus_read" },', 1)
        tail = tail.replace('notes: "', 'notes: "status: EngineCapabilityStatus.Parity, consumers: new[] { \\"genexus_edit\\" }. ', 1)
        partial = head + tail
        self.assertIn('genexus_read', partial)
        self.assertEqual(self.module.read_native_routed_operations(self._registry_with(partial)), operations)

        # The same consumer on a Parity capability is routed.
        parity = head + tail.replace("EngineCapabilityStatus.Partial", "EngineCapabilityStatus.Parity", 1)
        self.assertCountEqual(
            self.module.read_native_routed_operations(self._registry_with(parity)), operations + ["genexus_read"])

    def test_every_stale_coverage_number_is_rejected(self):
        operations = self.module.read_native_routed_operations(self.module.REGISTRY)
        for pattern, group in self.COVERAGE_SENTENCES:
            with self.subTest(pattern=pattern, group=group):
                stale = _bump_first_number(self.doc, pattern, group)
                with self.assertRaises(ValueError) as caught:
                    self.module.check_engine_doc(self.inventory, operations, self._doc_with(stale))
                self.assertIn("coverage says", str(caught.exception))

    def test_a_new_routed_operation_makes_the_unchanged_doc_stale(self):
        routed = {operation.partition(".")[0] for operation in
                  self.module.read_native_routed_operations(self.module.REGISTRY)}
        candidate = next(entry["tool"] for entry in self.inventory["tools"]
                         if [row["action"] for row in entry["actions"]] == [None] and entry["tool"] not in routed)
        widened = self.registry.replace(
            'consumers: new[] { "genexus_inspect" }', 'consumers: new[] { "genexus_inspect", "%s" }' % candidate, 1)
        self.assertNotEqual(widened, self.registry)
        operations = self.module.read_native_routed_operations(self._registry_with(widened))
        with self.assertRaises(ValueError) as caught:
            self.module.check_engine_doc(self.inventory, operations, self.module.ENGINE_DOC)
        self.assertIn("coverage says", str(caught.exception))

    def test_routed_operations_must_name_published_rows(self):
        coverage = self.module.native_coverage
        with self.assertRaisesRegex(ValueError, "names no published operation row"):
            coverage(self.inventory, ["genexus_kb.__not_an_action__"])
        # A tool with an action selector has no single row for a bare tool name.
        with self.assertRaisesRegex(ValueError, "names no published operation row"):
            coverage(self.inventory, ["genexus_kb"])
        with self.assertRaisesRegex(ValueError, "tool that is not published"):
            coverage(self.inventory, ["genexus_not_a_tool"])

    def test_unreadable_routing_rule_fails_closed(self):
        changed = self.registry.replace(
            "Status == EngineCapabilityStatus.Parity;", "Status != EngineCapabilityStatus.Unsupported;", 1)
        self.assertNotEqual(changed, self.registry)
        with self.assertRaises(ValueError) as caught:
            self.module.read_native_routed_operations(self._registry_with(changed))
        self.assertIn("MayRouteToEngine", str(caught.exception))

    def test_check_command_fails_when_the_doc_disagrees_with_the_source(self):
        stale = _bump_first_number(self.doc, r"Those are \*\*(\d+) of", 1)
        self.module.ENGINE_DOC = self._doc_with(stale)
        self.module.verify_classifier_policy = lambda: None
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            exit_code = self.module.main(["--check"])
        self.assertEqual(exit_code, 2)
        self.assertIn("coverage says", errors.getvalue())


if __name__ == "__main__":
    unittest.main()
