"""Compare gx-engine typed execution with an already verified native fixture run.

Reads local authored evidence only; no KB mutation, SDK load or native build.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile

from typed_semantics_cases import EXPECTED, cases, compare_output, native_source, variables


def portable_project():
    declarations = {}
    for variable in variables:
        if variable['typeName'] == 'Boolean':
            data_type = {'kind': 'boolean'}
        else:
            data_type = {'kind': 'numeric', 'precision': variable['length'],
                         'scale': variable.get('decimals', 0)}
        declarations[variable['varName']] = data_type
    admitted = [case for case in cases if case[0] != 'integer_storage_wrap']
    body = []
    for label, source, output in admitted:
        suffix = '.ToString().Trim()'
        if not output.endswith(suffix):
            raise ValueError('unhandled native rendering expression: ' + output)
        body.append(source + '\nPrint(' + output[:-len(suffix)] + ')')
    return {'format_version': 2, 'entry': 'Main', 'procedures': [
        {'name': 'Main', 'params': [], 'variables': declarations, 'source': '\n'.join(body)}
    ]}, [label for label, _, _ in admitted]


def validate(engine, evidence):
    native = json.loads((evidence / 'report.json').read_text())
    authored = json.loads((evidence / 'authored.json').read_text())
    if not native.get('source_verified') or native.get('exit') != 0:
        raise ValueError('native executable evidence not verified')
    if authored['source'] != native_source() or authored['variables'] != variables:
        raise ValueError('native evidence does not match the complete authored corpus')
    compare_output(native['stdout'])
    project, labels = portable_project()
    with tempfile.TemporaryDirectory(prefix='gx-typed-differential-') as temporary:
        path = Path(temporary) / 'typed.json'
        path.write_text(json.dumps(project), encoding='utf-8')
        run = subprocess.run([str(engine), 'run', str(path), str(Path(temporary) / 'out')],
                             capture_output=True, text=True, timeout=140)
    if run.returncode:
        raise RuntimeError('typed compilation/execution failed: ' + run.stderr)
    observed = run.stdout.splitlines()
    expected = [EXPECTED[label] for label in labels]
    if observed != expected:
        raise ValueError(f'typed executable differs: expected {expected!r}, got {observed!r}')
    return {'matched': len(labels), 'labels': labels, 'native_evidence': str(evidence),
            'excluded': {'integer_storage_wrap': 'explicit checked-storage safety divergence'},
            'scope': 'one parameterless typed procedure; not full SDK parity',
            'stdout': run.stdout}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--engine', type=Path, required=True)
    parser.add_argument('--native-evidence', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    report = validate(args.engine.resolve(), args.native_evidence.resolve())
    args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
