"""Authored observations from the GeneXus 18 .NET Boolean/Numeric oracle.

Expectations describe native rendering/storage, not gx-engine support.
Overflow behavior is evidence to diagnose, not a safe-engine mandate.
"""
variables = [
    {'varName':'Amount','typeName':'Numeric','length':5,'decimals':2},
    {'varName':'Whole','typeName':'Numeric','length':3,'decimals':0},
    {'varName':'Other','typeName':'Numeric','length':5,'decimals':2},
    {'varName':'Flag','typeName':'Boolean'},
]
cases = [
    ('positive_half', '&Amount = 1.235', '&Amount.ToString().Trim()'),
    ('negative_half', '&Amount = -1.235', '&Amount.ToString().Trim()'),
    ('below_half', '&Amount = 1.234', '&Amount.ToString().Trim()'),
    ('positive_integer_half', '&Whole = 1.5', '&Whole.ToString().Trim()'),
    ('negative_integer_half', '&Whole = -1.5', '&Whole.ToString().Trim()'),
    ('numeric_overflow', '&Whole = 1000', '&Whole.ToString().Trim()'),
    ('decimal_overflow', '&Amount = 1000.12', '&Amount.ToString().Trim()'),
    ('negative_overflow', '&Whole = -1000', '&Whole.ToString().Trim()'),
    ('decimal_add', '&Amount = 0.1\n&Other = 0.2\n&Amount = &Amount + &Other', '&Amount.ToString().Trim()'),
    ('division', '&Amount = 1 / 8', '&Amount.ToString().Trim()'),
    ('negative_division', '&Amount = -1 / 8', '&Amount.ToString().Trim()'),
    ('assignment_rescale', '&Amount = 1.55\n&Whole = &Amount', '&Whole.ToString().Trim()'),
    ('decimal_storage_precision', '&Amount = 1.235\n&Whole = 0\nIf &Amount = 1.235\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('decimal_storage_after_add', '&Amount = 1.235\n&Other = 0.001\n&Amount = &Amount + &Other\n&Whole = 0\nIf &Amount = 1.236\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('width_not_integer_range', '&Whole = 1000\n&Flag = &Whole = 1000\n&Whole = 0\nIf &Flag\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('width_not_decimal_range', '&Amount = 1000.12\n&Whole = 0\nIf &Amount = 1000.12\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('integer_storage_wrap', '&Whole = 32760\n&Whole = &Whole + 10\n&Flag = &Whole < 0\n&Whole = 0\nIf &Flag\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('boolean_true', '&Flag = True\n&Whole = 0\nIf &Flag\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('boolean_not', '&Flag = Not True\n&Whole = 0\nIf &Flag\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
    ('boolean_comparison', '&Flag = 2 < 3 And 4 = 4\n&Whole = 0\nIf &Flag\n&Whole = 1\nEndIf', '&Whole.ToString().Trim()'),
]

EXPECTED = {
    'positive_half': '1.24', 'negative_half': '-1.24', 'below_half': '1.23',
    'positive_integer_half': '1', 'negative_integer_half': '-1',
    'numeric_overflow': '***', 'decimal_overflow': '*****',
    'negative_overflow': '***', 'decimal_add': '0.30', 'division': '0.13',
    'negative_division': '-0.13', 'assignment_rescale': '1',
    'decimal_storage_precision': '1', 'decimal_storage_after_add': '1',
    'width_not_integer_range': '1', 'width_not_decimal_range': '1',
    'integer_storage_wrap': '1', 'boolean_true': '1', 'boolean_not': '0',
    'boolean_comparison': '1',
}


def native_source():
    return '\n'.join(body+'\nmsg("'+label+'=" + '+output+', status)'
                     for label, body, output in cases)


def compare_output(stdout):
    lines = stdout.splitlines()
    expected = [label+'='+EXPECTED[label] for label, _, _ in cases]
    if lines != expected:
        raise ValueError('native typed observations differ: expected '
                         + repr(expected) + ', observed ' + repr(lines))
    return len(expected)
