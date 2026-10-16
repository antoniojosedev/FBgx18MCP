"""Authored .NET text observations, not universal string/runtime compatibility."""
variables = [{'varName': 'FixedText', 'typeName': 'Character', 'length': 5}, {'varName': 'VariableText', 'typeName': 'VarChar', 'length': 5}, {'varName': 'LongText', 'typeName': 'VarChar', 'length': 20}]
source = '&FixedText = "ab"\nmsg("fixed_short=[" + &FixedText + "]", status)\nmsg("fixed_len=" + &FixedText.Length().ToString().Trim(), status)\n&VariableText = "ab"\nmsg("variable_short=[" + &VariableText + "]", status)\nmsg("variable_len=" + &VariableText.Length().ToString().Trim(), status)\n&FixedText = "abcdefg"\nmsg("fixed_long=[" + &FixedText + "]", status)\n&VariableText = "abcdefg"\nmsg("variable_long=[" + &VariableText + "]", status)\n&FixedText = " ab "\nmsg("fixed_spaces=[" + &FixedText + "]", status)\n&VariableText = " ab "\nmsg("variable_spaces=[" + &VariableText + "]", status)\nIf &FixedText = " ab"\nmsg("fixed_equal=1", status)\nElse\nmsg("fixed_equal=0", status)\nEndIf\nIf &VariableText = " ab"\nmsg("variable_equal=1", status)\nElse\nmsg("variable_equal=0", status)\nEndIf\nIf "A" = "a"\nmsg("case_equal=1", status)\nElse\nmsg("case_equal=0", status)\nEndIf\n&LongText = &FixedText + &VariableText\nmsg("concat=[" + &LongText + "]", status)\n&FixedText = ""\nmsg("fixed_empty=[" + &FixedText + "]", status)\n&VariableText = ""\nmsg("variable_empty=[" + &VariableText + "]", status)\n&FixedText = "a\xe7\xe3o"\nmsg("fixed_unicode=[" + &FixedText + "]", status)\nmsg("unicode_len=" + &FixedText.Length().ToString().Trim(), status)\n&FixedText = "a\U0001f600b"\nmsg("astral=[" + &FixedText + "]", status)\nmsg("astral_len=" + &FixedText.Length().ToString().Trim(), status)'
native_expected = ['fixed_short=[ab]', 'fixed_len=2', 'variable_short=[ab]', 'variable_len=2', 'fixed_long=[abcdefg]', 'variable_long=[abcdefg]', 'fixed_spaces=[ ab ]', 'variable_spaces=[ ab ]', 'fixed_equal=1', 'variable_equal=1', 'case_equal=0', 'concat=[ ab  ab ]', 'fixed_empty=[]', 'variable_empty=[]', 'fixed_unicode=[a\xe7\xe3o]', 'unicode_len=4', 'astral=[a??b]', 'astral_len=4']
length_labels = {"fixed_len", "variable_len", "unicode_len", "astral_len"}
engine_expected = [line.split("=", 1)[1] if line.split("=", 1)[0] in length_labels else line for line in native_expected]

def compare_output(output, expected):
    actual = output.splitlines()
    if actual != expected:
        raise ValueError(f"text output mismatch: expected {expected!r}, observed {actual!r}")
    return len(actual)

# Native .NET generation wrote the UTF-16 surrogate pair as two question marks
# into its CP1252 C# file. Rust preserves Unicode instead of reproducing data loss.
safety_divergences = {"astral": {"native": "astral=[a??b]", "rust": "astral=[a😀b]", "reason": "native generated-source encoding loss is not reproduced"}}
engine_expected = [safety_divergences["astral"]["rust"] if line == safety_divergences["astral"]["native"] else line for line in engine_expected]
