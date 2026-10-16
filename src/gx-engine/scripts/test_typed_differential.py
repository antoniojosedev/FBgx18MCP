import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from typed_semantics_cases import EXPECTED, cases, native_source, variables
from validate_typed import portable_project, validate


class TypedDifferentialTests(unittest.TestCase):
    def fixture(self, folder):
        path = Path(folder)
        (path / 'authored.json').write_text(json.dumps({'source': native_source(), 'variables': variables}))
        stdout = '\n'.join(label + '=' + EXPECTED[label] for label, _, _ in cases) + '\n'
        (path / 'report.json').write_text(json.dumps({'source_verified': True, 'exit': 0, 'stdout': stdout}))
        return path

    def test_portable_corpus_keeps_bodies_and_excludes_only_wrapping(self):
        project, labels = portable_project()
        admitted = [case for case in cases if case[0] != 'integer_storage_wrap']
        self.assertEqual(labels, [label for label, _, _ in admitted])
        self.assertEqual(project['format_version'], 2)
        for _, body, _ in admitted:
            self.assertIn(body, project['procedures'][0]['source'])
        self.assertEqual(len(project['procedures'][0]['variables']), len(variables))

    def test_compares_the_complete_executable_output(self):
        _, labels = portable_project()
        output = '\n'.join(EXPECTED[label] for label in labels) + '\n'
        with tempfile.TemporaryDirectory() as folder, patch('validate_typed.subprocess.run') as run:
            evidence = self.fixture(folder)
            run.return_value = subprocess.CompletedProcess([], 0, output, '')
            self.assertEqual(validate(Path('engine.exe'), evidence)['matched'], 19)
            for wrong in [output + 'extra\n', output.replace('1.24', '1.23', 1), '']:
                run.return_value = subprocess.CompletedProcess([], 0, wrong, '')
                with self.assertRaises(ValueError):
                    validate(Path('engine.exe'), evidence)

    def test_rejects_stale_native_source_before_running_rust(self):
        with tempfile.TemporaryDirectory() as folder, patch('validate_typed.subprocess.run') as run:
            evidence = self.fixture(folder)
            (evidence / 'authored.json').write_text(json.dumps({'source': '', 'variables': variables}))
            with self.assertRaises(ValueError):
                validate(Path('engine.exe'), evidence)
            run.assert_not_called()

    def test_rejects_failed_native_and_rust_executions(self):
        with tempfile.TemporaryDirectory() as folder, patch('validate_typed.subprocess.run') as run:
            evidence = self.fixture(folder)
            report = json.loads((evidence / 'report.json').read_text())
            report['exit'] = 1
            (evidence / 'report.json').write_text(json.dumps(report))
            with self.assertRaises(ValueError):
                validate(Path('engine.exe'), evidence)
            run.assert_not_called()
            evidence = self.fixture(folder)
            run.return_value = subprocess.CompletedProcess([], 70, '', 'controlled runtime error')
            with self.assertRaises(RuntimeError):
                validate(Path('engine.exe'), evidence)


if __name__ == '__main__':
    unittest.main()
