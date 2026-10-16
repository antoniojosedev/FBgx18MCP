"""Tests of oracle evidence validation, not of Rust decimal support."""
import unittest

from typed_semantics_cases import EXPECTED, cases, compare_output, native_source, variables


class TypedSemanticsCorpusTests(unittest.TestCase):
    def output(self):
        return '\n'.join(label+'='+EXPECTED[label] for label, _, _ in cases)+'\n'

    def test_every_case_has_unique_expectation_and_authored_declarations(self):
        labels = [label for label, _, _ in cases]
        self.assertEqual(len(labels), 20)
        self.assertEqual(len(set(labels)), len(labels))
        self.assertEqual(set(labels), set(EXPECTED))
        self.assertEqual([v['varName'] for v in variables], ['Amount', 'Whole', 'Other', 'Flag'])
        self.assertEqual(variables[0], {'varName':'Amount', 'typeName':'Numeric', 'length':5, 'decimals':2})
        self.assertIn('&Amount = 1.235', native_source())

    def test_exact_observations_match_with_windows_or_unix_line_endings(self):
        self.assertEqual(compare_output(self.output()), 20)
        self.assertEqual(compare_output(self.output().replace('\n', '\r\n')), 20)

    def test_missing_extra_duplicate_and_changed_observations_fail(self):
        rows = self.output().splitlines()
        for altered in [rows[:-1], rows+['extra=1'], rows+[rows[0]],
                        list(reversed(rows)), ['positive_half=1.23']+rows[1:]]:
            with self.subTest(altered=altered):
                with self.assertRaises(ValueError):
                    compare_output('\n'.join(altered))

    def test_storage_and_rendering_observations_remain_distinct(self):
        self.assertEqual(EXPECTED['positive_half'], '1.24')
        self.assertEqual(EXPECTED['decimal_storage_precision'], '1')
        self.assertEqual(EXPECTED['numeric_overflow'], '***')
        self.assertEqual(EXPECTED['width_not_integer_range'], '1')
        self.assertEqual(EXPECTED['integer_storage_wrap'], '1')


if __name__ == '__main__':
    unittest.main()
