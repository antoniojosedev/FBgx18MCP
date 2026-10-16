"""Authored shared-subset cases; expected values are independent of either compiler.

Native procedures use Numeric(18,0), not the engine's full signed-i64 domain.
Integer division cases deliberately expose GeneXus assignment/rounding differences.
"""
import random


def cases():
    rows = []
    def add(name, source, expected):
        rows.append({'name': name, 'source': source, 'expected': expected})
    for name, expression, expected in [
        ('precedence', '2 + 3 * 4', 14), ('parentheses', '(2 + 3) * 4', 20),
        ('left_subtraction', '20 - 7 - 2', 11), ('left_division', '48 / 4 / 3', 4),
        ('negative_zero', '-0', 0), ('unary', '-(-7)', 7),
        ('large_exact', '99999999999999999 + 1', 100000000000000000),
        ('large_multiply', '123456789 * 123456789', 15241578750190521),
        ('cancellation', '999999999999999999 - 999999999999999998', 1),
    ]:
        add(name, f'Print({expression})', [expected])
    for a, b in [(7, 2), (-7, 2), (7, -2), (-7, -2), (1, 2), (-1, 2), (9, 4)]:
        expected = abs(a) // abs(b) * (-1 if (a < 0) != (b < 0) else 1)
        add(f'division_{a}_{b}', f'Print({a} / {b})', [expected])
    for op, expected in [('=', 0), ('<>', 1), ('<', 1), ('<=', 1), ('>', 0), ('>=', 0)]:
        add(f'branch_{op}', f'If -2 {op} 3\nPrint(1)\nElse\nPrint(0)\nEndIf', [expected])
    for op, expected in [('=', 1), ('<>', 0), ('<', 0), ('<=', 1), ('>', 0), ('>=', 1)]:
        add(f'equal_branch_{op}', f'If 3 {op} 3\nPrint(1)\nElse\nPrint(0)\nEndIf', [expected])
    for left in (0, 1):
        for right in (0, 1):
            for operator in ('And', 'Or'):
                expected = int((bool(left) and bool(right)) if operator == 'And' else (bool(left) or bool(right)))
                add(f'logical_{operator}_{left}_{right}',
                    f'If {left} = 1 {operator} {right} = 1\nPrint(1)\nElse\nPrint(0)\nEndIf', [expected])
    for name, condition, expected in [
        ('logical_precedence', '1 = 1 Or 1 = 2 And 2 = 3', 1),
        ('logical_grouping', '(1 = 1 Or 1 = 2) And 2 = 3', 0),
        ('logical_not', 'Not 1 = 2 And Not (3 < 2 Or 4 <> 4)', 1),
        ('logical_double_not', 'Not Not 1 = 1', 1),
        ('logical_case', 'nOt (1 = 2) aNd 3 = 3 oR 4 = 5', 1),
    ]:
        add(name, f'If {condition}\nPrint(1)\nElse\nPrint(0)\nEndIf', [expected])
    add('logical_loop', '&i = 0\nDo While &i < 3 And Not &i = 2\n&i = &i + 1\nEndDo\nPrint(&i)', [2])
    add('logical_short_circuit_and', '&x = 0\nIf &x <> 0 And 10 / &x > 1\nPrint(99)\nElse\nPrint(1)\nEndIf', [1])
    add('logical_short_circuit_or', '&x = 0\nIf &x = 0 Or 10 / &x > 1\nPrint(1)\nElse\nPrint(99)\nEndIf', [1])
    add('large_variable_multiply', '&x = 123456789\nPrint(&x * &x)', [15241578750190521])
    add('assignment_copy', '&x = 7\n&s = &x\n&x = 9\nPrint(&s)\nPrint(&x)', [7, 9])
    add('nested_branch', 'If 2 < 3\nIf 5 = 6\nPrint(99)\nElse\nPrint(8)\nEndIf\nElse\nPrint(77)\nEndIf', [8])
    add('zero_loop', '&i = 5\nDo While &i < 0\nPrint(99)\nEndDo\nPrint(&i)', [5])
    add('nested_loop', '&i = 0\n&s = 0\nDo While &i < 3\n&j = 0\nDo While &j < 4\n&s = &s + &i * 10 + &j\n&j = &j + 1\nEndDo\n&i = &i + 1\nEndDo\nPrint(&s)', [138])
    add('both_branch_assignment', 'If 1 = 1\n&x = 7\nElse\n&x = 8\nEndIf\nPrint(&x)', [7])
    add('case_insensitive_variable', '&Number = 17\nPrint(&nUmBeR)', [17])
    add('overwriting', '&x = 3\n&x = &x * &x\nPrint(&x)', [9])
    rng = random.Random(890129)
    for index in range(40):
        a, b, c = (rng.randint(-10000, 10000) for _ in range(3))
        add(f'seeded_{index}', f'Print(({a} + {b}) * {c})', [(a + b) * c])
    return rows


def source_for(rows, native=False):
    lines = []
    for row in rows:
        for line in row['source'].splitlines():
            if native and line.startswith('Print('):
                lines.extend(['&Value = ' + line[6:-1], 'msg(&Value.ToString().Trim(), status)'])
            else:
                lines.append(line)
    return '\n'.join(lines)


def compare(rows, observed):
    """One row per case, retaining missing/extra output as failures, never zip-only."""
    results = []
    offset = 0
    for row in rows:
        end = offset + len(row['expected'])
        actual = observed[offset:end]
        results.append({'name': row['name'], 'expected': row['expected'],
                        'actual': actual, 'matches': actual == row['expected']})
        offset = end
    if len(observed) > offset:
        results.append({'name': 'unexpected_extra_output', 'expected': [],
                        'actual': observed[offset:], 'matches': False})
    return results
