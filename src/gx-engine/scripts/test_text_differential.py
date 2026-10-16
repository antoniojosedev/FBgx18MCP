import json
from pathlib import Path
import tempfile
import unittest

from string_semantics_cases import compare_output, engine_expected, native_expected, source, variables
from validate_text import verify_evidence


class TextDifferentialTests(unittest.TestCase):
    def test_expected_outputs_reject_missing_extra_changed_and_reordered_lines(self):
        self.assertEqual(compare_output('\r\n'.join(engine_expected), engine_expected), 18)
        for candidate in [engine_expected[:-1], engine_expected + ['extra'], list(reversed(engine_expected)), ['changed'] + engine_expected[1:]]:
            with self.assertRaises(ValueError):
                compare_output('\n'.join(candidate), engine_expected)

    def test_evidence_checks_authored_source_native_state_and_declarations(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            authored = {'source': source, 'variables': variables}
            report = {'exit': 0, 'source_verified': True, 'stdout': '\n'.join(native_expected)}
            declarations = [{'name': '&' + item['varName'], 'type': item['typeName'], 'length': item['length']} for item in variables]
            def save():
                for name, value in [('authored.json', authored), ('report.json', report),
                                    ('readback-Source.json', {'result': {'structuredContent': {'source': source}}}),
                                    ('readback-Variables.json', {'result': {'structuredContent': {'variables': declarations}}})]:
                    (root / name).write_text(json.dumps(value), encoding='utf-8')
            save()
            self.assertEqual(verify_evidence(root), report)
            report['exit'] = 70
            save()
            with self.assertRaises(ValueError): verify_evidence(root)
            report['exit'] = 0
            declarations[0]['length'] = 6
            save()
            with self.assertRaises(ValueError): verify_evidence(root)
            declarations[0]['length'] = 5
            authored['source'] += '\nPrint(1)'
            save()
            with self.assertRaises(ValueError): verify_evidence(root)


if __name__ == '__main__':
    unittest.main()
