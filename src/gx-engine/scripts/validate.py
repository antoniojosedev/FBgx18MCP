"""Deterministic subset oracle and warm-host build measurements; never opens a KB."""
import argparse
import json
import os
from pathlib import Path
import platform
import random
import statistics
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]


def execute(args, expected=0, timeout=150):
    started = time.perf_counter()
    result = subprocess.run([str(arg) for arg in args], capture_output=True,
                            text=True, timeout=timeout)
    elapsed = (time.perf_counter() - started) * 1000
    if expected is not None and result.returncode != expected:
        raise RuntimeError(f"{args[0]} returned {result.returncode}: {result.stderr[:2000]}")
    return result, elapsed


def expression(rng, depth):
    """Return source and independent Python integer result (no engine AST/codegen)."""
    if depth == 0:
        value = rng.randint(-20, 20)
        return str(value), value
    left, a = expression(rng, depth - 1)
    right, b = expression(rng, depth - 1)
    op = rng.choice(['+', '-', '*', '/', '=', '<>', '<', '<=', '>', '>='])
    if op == '/':
        if b == 0:
            right, b = '1', 1
        value = (abs(a) // abs(b)) * (-1 if (a < 0) != (b < 0) else 1)
    else:
        value = {'+': lambda: a + b, '-': lambda: a - b, '*': lambda: a * b,
                 '=': lambda: int(a == b), '<>': lambda: int(a != b),
                 '<': lambda: int(a < b), '<=': lambda: int(a <= b),
                 '>': lambda: int(a > b), '>=': lambda: int(a >= b)}[op]()
    return f'({left} {op} {right})', value


def project(source):
    return {'format_version': 1, 'entry': 'Main', 'procedures': [
        {'name': 'Main', 'params': [], 'source': source}]}


def measure(args, count):
    samples = [execute(args)[1] for _ in range(count)]
    return {'samples_ms': samples, 'median_ms': statistics.median(samples),
            'min_ms': min(samples), 'max_ms': max(samples)}


def validate(engine, directory, cases, repeats, seed):
    rng = random.Random(seed)
    corpus = [expression(rng, 3) for _ in range(cases)]
    # Explicit boundaries complement small randomized arithmetic.
    corpus += [('-9223372036854775808', -(2**63)),
               ('9223372036854775807', 2**63 - 1), ('-7 / 2', -3)]
    source = '\n'.join(f'Print({text})' for text, _ in corpus)
    # Branches, variable state, finite loops and their observable ordering.
    source += ('\n&i = 0\nDo While &i < 10\n'
               'If &i < 5\nPrint(&i * 2)\nElse\nPrint(-&i)\nEndIf\n'
               '&i = &i + 1\nEndDo')
    expected = [value for _, value in corpus] + [i * 2 if i < 5 else -i for i in range(10)]
    input_path = directory / 'corpus.json'
    input_path.write_text(json.dumps(project(source)), encoding='utf-8')
    result, _ = execute([engine, 'build', input_path, directory / 'builds'])
    binary = Path(result.stdout.strip())
    output, _ = execute([binary], timeout=15)
    if output.stdout.splitlines() != [str(value) for value in expected]:
        raise AssertionError('independent numeric/control-flow oracle mismatch')

    rejected = [
        'Print(&undefined)', 'Print(Missing())', 'Print(1, 2)',
        'If 1\nPrint(1)', 'Print(9223372036854775808)',
        'Print("unsupported")', 'For each\nEndFor',
        'Do While 0\n&x = 1\nEndDo\nPrint(&x)',
    ]
    for body in rejected:
        input_path.write_text(json.dumps(project(body)), encoding='utf-8')
        result, _ = execute([engine, 'check', input_path], expected=None, timeout=10)
        if result.returncode != 1 or not result.stderr.strip():
            raise AssertionError(f'expected controlled rejection: {body!r}: {result}')

    runtime_errors = ['Print(9223372036854775807 + 1)', 'Print(1 / 0)',
                      'Print(-9223372036854775808 / -1)',
                      'Print(-(-9223372036854775808))', 'Do While 1\nEndDo']
    for body in runtime_errors:
        input_path.write_text(json.dumps(project(body)), encoding='utf-8')
        result, _ = execute([engine, 'build', input_path, directory / 'errors'])
        failed, _ = execute([Path(result.stdout.strip())], expected=None, timeout=15)
        if failed.returncode == 0 or 'gx-engine runtime error:' not in failed.stderr:
            raise AssertionError(f'expected controlled runtime error: {body!r}')

    input_path.write_text(json.dumps(project(source)), encoding='utf-8')
    generated = binary.parent / 'program.rs'
    direct = directory / ('direct.exe' if os.name == 'nt' else 'direct')
    rustc_args = ['rustc', '--crate-name', 'gx_program', '--edition=2024',
                  '-C', 'opt-level=1', '-C', 'overflow-checks=yes', generated, '-o', direct]
    execute(rustc_args)  # Warm-up is excluded, as is the earlier engine build.
    direct_output, _ = execute([direct], timeout=15)
    if direct_output.stdout != output.stdout:
        raise AssertionError('direct rustc observable output differs')
    # Alternate order to reduce monotonic load/temperature bias.
    samples = {'engine_build': [], 'direct_rustc': []}
    commands = {'engine_build': [engine, 'build', input_path, directory / 'timings'],
                'direct_rustc': rustc_args}
    for iteration in range(repeats):
        order = list(commands) if iteration % 2 == 0 else list(reversed(commands))
        for name in order:
            built, elapsed = execute(commands[name])
            samples[name].append(elapsed)
            artifact = Path(built.stdout.strip()) if name == 'engine_build' else direct
            observed, _ = execute([artifact], timeout=15)
            if observed.stdout != output.stdout:
                raise AssertionError(f'{name} timed-build output differs')
    builds = {name: {'samples_ms': values, 'median_ms': statistics.median(values),
                     'min_ms': min(values), 'max_ms': max(values)}
              for name, values in samples.items()}
    return {'seed': seed, 'expression_cases': len(corpus), 'observed_values': len(expected),
            'compile_rejections': len(rejected), 'runtime_errors': len(runtime_errors),
            'builds': builds, 'engine_executable_run': measure([binary], repeats),
            'direct_executable_run': measure([direct], repeats),
            'genexus_comparison': 'not run: this standalone validator never opens GeneXus; see VALIDATION.md for live evidence',
            'scope': 'warm host, fresh native builds, same emitted Rust/flags; not GeneXus parity'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cases', type=int, default=200)
    parser.add_argument('--repeats', type=int, default=5)
    parser.add_argument('--seed', type=int, default=129)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.cases <= 500 or not 3 <= args.repeats <= 20:
        parser.error('cases must be 1..500 and repeats 3..20')
    execute(['cargo', 'build', '--release', '--offline', '--locked',
             '--manifest-path', ROOT / 'Cargo.toml'])
    engine = ROOT / 'target' / 'release' / ('gx-engine.exe' if os.name == 'nt' else 'gx-engine')
    with tempfile.TemporaryDirectory(prefix='gx-engine-validation-') as temp:
        report = validate(engine, Path(temp), args.cases, args.repeats, args.seed)
    report['host'] = {'platform': platform.platform(), 'cpu': platform.processor(),
                      'logical_cpus': os.cpu_count(),
                      'rustc': execute(['rustc', '-vV'])[0].stdout.strip()}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
