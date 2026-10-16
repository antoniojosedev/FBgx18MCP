"""Offline edge corpus and fail-closed CLI checks; no SDK or KB access."""
import argparse
import json
from pathlib import Path
import tempfile
from edge_corpus import cases, compare, source_for
from validate import execute, project


def validate_edges(engine, directory):
    input_path = directory / 'input.json'
    rows = cases()
    input_path.write_text(json.dumps(project(source_for(rows))), encoding='utf-8')
    built, _ = execute([engine, 'build', input_path])
    output, _ = execute([Path(built.stdout.strip())], timeout=15)
    results = compare(rows, [int(line) for line in output.stdout.splitlines()])
    if any(not row['matches'] for row in results):
        raise AssertionError(results)

    # Empty procedures intentionally fall through with zero; they are not malformed.
    input_path.write_text(json.dumps(project('')), encoding='utf-8')
    empty, _ = execute([engine, 'build', input_path])
    empty_output, _ = execute([Path(empty.stdout.strip())], timeout=15)
    if empty_output.stdout:
        raise AssertionError('empty procedure unexpectedly printed output')

    rejected = [
        'Print()', 'Print(1,2)', 'Print(&Missing)', 'Print(Missing())',
        'Print(1 % 2)', 'Print(1 != 2)', 'Print(1 ** 2)', 'Print(1.5)',
        'Print("text")', 'Print(9223372036854775808)',
        'Print(-9223372036854775809)', 'Print(1) trailing',
        'If 1\nPrint(1)', 'Else\nPrint(1)', 'EndIf', 'EndDo',
        'Do While 1\nPrint(1)', 'For each\nEndFor',
        'If 1\n&x = 1\nEndIf\nPrint(&x)',
        'Do While 0\n&x = 1\nEndDo\nPrint(&x)',
        'Print(' + '(' * 200 + '1' + ')' * 200 + ')',
        'Print(' + '+'.join(['1'] * 300) + ')',
        'If 1 = 1 And\nEndIf', 'If Or 1 = 1\nEndIf',
        'If Not\nEndIf', 'If 1 = 1 Or Or 2 = 2\nEndIf',
        'Print(' + 'Not ' * 200 + '1)',
        'Print(' + ' And '.join(['1'] * 300) + ')',
    ]
    for source in rejected:
        input_path.write_text(json.dumps(project(source)), encoding='utf-8')
        result, _ = execute([engine, 'check', input_path], expected=None, timeout=10)
        if result.returncode != 1 or not result.stderr.strip():
            raise AssertionError(f'not a controlled rejection: {source[:80]!r}: {result}')

    invalid_documents = [b'{', b'null', b'[]', b'{}', b'\xff',
                         json.dumps({**project('Print(1)'), 'format_version': 99}).encode(),
                         json.dumps({**project('Print(1)'), 'entry': 'Missing'}).encode(),
                         b' ' * (1024 * 1024 + 1)]
    for document in invalid_documents:
        input_path.write_bytes(document)
        result, _ = execute([engine, 'check', input_path], expected=None, timeout=10)
        if result.returncode != 1 or not result.stderr.strip():
            raise AssertionError(f'invalid document did not fail closed: {result}')

    errors = ['Print(9223372036854775807 + 1)',
              'Print(-9223372036854775808 - 1)',
              'Print(9223372036854775807 * 2)', 'Print(1 / 0)',
              'Print(-9223372036854775808 / -1)',
              'Print(-(-9223372036854775808))', 'Do While 1\nEndDo']
    for source in errors:
        input_path.write_text(json.dumps(project(source)), encoding='utf-8')
        built, _ = execute([engine, 'build', input_path])
        result, _ = execute([Path(built.stdout.strip())], expected=None, timeout=15)
        if result.returncode == 0 or 'gx-engine runtime error:' not in result.stderr:
            raise AssertionError(f'not a controlled runtime failure: {source!r}: {result}')
    return {'result_cases': len(rows), 'outputs': len(output.stdout.splitlines()),
            'source_rejections': len(rejected), 'invalid_documents': len(invalid_documents),
            'controlled_runtime_errors': len(errors),
            'scope': 'numeric subset only; native differential evidence is separate'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='gx-edge-validation-') as temp:
        report = validate_edges(args.engine.resolve(), Path(temp))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
