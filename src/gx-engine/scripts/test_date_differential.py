import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import subprocess

from date_semantics_cases import native_expected, source, variables
from validate_dates import compare_output, validate, verify_evidence


class DateDifferentialTests(unittest.TestCase):
    def test_comparison_rejects_missing_extra_reordered_and_changed_values(self):
        self.assertEqual(compare_output('\r\n'.join(native_expected), native_expected), 37)
        for candidate in [native_expected[:-1], native_expected + ['extra'], list(reversed(native_expected)), ['changed'] + native_expected[1:]]:
            with self.assertRaises(ValueError):
                compare_output('\n'.join(candidate), native_expected)

    def test_native_evidence_is_source_and_declaration_matched_and_fail_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            authored = {'source': source, 'variables': variables}
            report = {'exit': 0, 'source_verified': True, 'stdout': '\n'.join(native_expected)}
            declarations = [{'name': '&' + v['varName'], 'type': v['typeName'], 'length': v.get('length')} for v in variables]
            source_result = {'source': source}
            def save():
                for name, value in [('authored.json', authored), ('report.json', report),
                                    ('readback-Source.json', {'result': {'structuredContent': source_result}}),
                                    ('readback-Variables.json', {'result': {'structuredContent': {'variables': declarations}}})]:
                    (root / name).write_text(json.dumps(value), encoding='utf-8')
            save()
            self.assertEqual(verify_evidence(root), report)
            for obj, key, value in [(report, 'exit', 1), (report, 'source_verified', False),
                                    (report, 'stdout', ''), (authored, 'source', 'stale'),
                                    (source_result, 'source', 'stale'), (source_result, 'truncated', True),
                                    (declarations[0], 'type', 'VARCHAR'), (declarations[2], 'length', 9),
                                    (declarations[2], 'decimals', 1)]:
                previous = obj.copy()
                obj[key] = value
                save()
                with self.assertRaises(ValueError): verify_evidence(root)
                obj.clear()
                obj.update(previous)

    def test_calendar_and_datetime_evidence_cannot_be_interchanged(self):
        import calendar_semantics_cases
        import datetime_semantics_cases
        for cases in [calendar_semantics_cases, datetime_semantics_cases]:
            with self.subTest(cases=cases.__name__), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                declarations = [{'name': '&' + v['varName'], 'type': v['typeName'],
                                 'length': v.get('length')} for v in cases.variables]
                files = {'authored.json': {'source': cases.source, 'variables': cases.variables},
                         'report.json': {'exit': 0, 'source_verified': True, 'stdout': '\n'.join(cases.native_expected)},
                         'readback-Source.json': {'result': {'structuredContent': {'source': cases.source}}},
                         'readback-Variables.json': {'result': {'structuredContent': {'variables': declarations}}}}
                for name, value in files.items():
                    (root / name).write_text(json.dumps(value), encoding='utf-8')
                self.assertEqual(verify_evidence(root, cases)['exit'], 0)
                with self.assertRaises(ValueError): verify_evidence(root)
                declarations[0]['type'] = 'VARCHAR'
                (root / 'readback-Variables.json').write_text(json.dumps(files['readback-Variables.json']), encoding='utf-8')
                with self.assertRaises(ValueError): verify_evidence(root, cases)

    def test_failed_rust_execution_is_not_counted_as_parity(self):
        with patch('validate_dates.verify_evidence'), patch('validate_dates.subprocess.run') as run:
            run.return_value = subprocess.CompletedProcess([], 70, '', 'failure')
            with self.assertRaises(ValueError): validate('engine', 'evidence')
            run.return_value = subprocess.CompletedProcess([], 0, '', '')
            with self.assertRaises(ValueError): validate('engine', 'evidence')
            run.side_effect = subprocess.TimeoutExpired([], 150)
            with self.assertRaises(subprocess.TimeoutExpired): validate('engine', 'evidence')


if __name__ == '__main__':
    unittest.main()
