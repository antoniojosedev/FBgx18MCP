import unittest
from edge_corpus import cases, compare, source_for


class EdgeCorpusTests(unittest.TestCase):
    def test_case_ids_are_unique_and_deterministic(self):
        rows = cases()
        self.assertEqual(rows, cases())
        self.assertEqual(len(rows), len({row['name'] for row in rows}))
        self.assertGreaterEqual(len(rows), 60)

    def test_native_translation_preserves_control_flow(self):
        rows = [{'source': 'If 1 = 1\nPrint(-7 / 2)\nEndIf'}]
        self.assertEqual(source_for(rows, True),
                         'If 1 = 1\n&Value = -7 / 2\nmsg(&Value.ToString().Trim(), status)\nEndIf')
        self.assertIn('Print(', source_for(rows))

    def test_missing_extra_and_wrong_outputs_fail(self):
        rows = [{'name': 'a', 'expected': [1]}, {'name': 'b', 'expected': [2]}]
        self.assertTrue(all(row['matches'] for row in compare(rows, [1, 2])))
        for output in ([1], [1, 2, 3], [1, 3], []):
            self.assertTrue(any(not row['matches'] for row in compare(rows, output)))

    def test_signed_division_oracle(self):
        rows = {row['name']: row['expected'] for row in cases()}
        self.assertEqual(rows['division_-7_2'], [-3])
        self.assertEqual(rows['division_7_-2'], [-3])
        self.assertEqual(rows['division_-7_-2'], [3])


if __name__ == '__main__':
    unittest.main()
