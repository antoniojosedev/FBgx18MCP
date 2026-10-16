import random
import unittest
from unittest.mock import Mock

from validate import expression


class OracleTests(unittest.TestCase):
    def evaluate(self, left, op, right):
        rng = Mock()
        rng.randint.side_effect = [left, right]
        rng.choice.return_value = op
        return expression(rng, 1)[1]

    def test_signed_division_truncates_toward_zero(self):
        for a, b, expected in [(-7, 2, -3), (7, -2, -3), (-7, -2, 3), (-1, 2, 0)]:
            with self.subTest(a=a, b=b):
                self.assertEqual(self.evaluate(a, '/', b), expected)

    def test_zero_divisor_is_replaced_in_source_and_oracle(self):
        rng = Mock()
        rng.randint.side_effect = [7, 0]
        rng.choice.return_value = '/'
        self.assertEqual(expression(rng, 1), ('(7 / 1)', 7))

    def test_all_operators(self):
        expected = {'+': 7, '-': -1, '*': 12, '/': 0, '=': 0,
                    '<>': 1, '<': 1, '<=': 1, '>': 0, '>=': 0}
        for op, value in expected.items():
            with self.subTest(op=op):
                self.assertEqual(self.evaluate(3, op, 4), value)
        self.assertEqual(self.evaluate(3, '=', 3), 1)
        self.assertEqual(self.evaluate(3, '<>', 3), 0)

    def test_corpus_is_reproducible_and_within_i64(self):
        first, second = random.Random(129), random.Random(129)
        for _ in range(500):
            source, value = expression(first, 3)
            self.assertEqual((source, value), expression(second, 3))
            self.assertGreaterEqual(value, -(2**63))
            self.assertLess(value, 2**63)


if __name__ == '__main__':
    unittest.main()
