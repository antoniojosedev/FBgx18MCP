#!/usr/bin/env python3
"""Differentially validate gx-engine Decimal operations against System.Decimal."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import tempfile

MAX_COEFFICIENT = (1 << 96) - 1


def decimal_text(coefficient: int, scale: int) -> str:
    negative = coefficient < 0
    digits = str(abs(coefficient))
    if scale:
        digits = digits.zfill(scale + 1)
        digits = f"{digits[:-scale]}.{digits[-scale:]}"
    return ("-" if negative else "") + digits


def generated_cases(count: int = 256) -> list[dict[str, str]]:
    rng = random.Random(129)
    operations = ("add", "sub", "mul", "div")
    cases = []
    for index in range(count):
        operation = operations[index % len(operations)]
        left = rng.randint(-MAX_COEFFICIENT, MAX_COEFFICIENT)
        right = rng.randint(-MAX_COEFFICIENT, MAX_COEFFICIENT)
        if operation == "div" and right == 0:
            right = 1
        cases.append(
            {
                "id": f"seed-129-{index:03d}",
                "operation": operation,
                "left": decimal_text(left, rng.randrange(29)),
                "right": decimal_text(right, rng.randrange(29)),
            }
        )
    return cases


def run(command: list[str], *, cwd: Path, env: dict[str, str] | None = None) -> str:
    completed = subprocess.run(
        command,
        cwd=cwd,
        env=env,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
    )
    if completed.returncode:
        sys.stderr.write(completed.stdout)
        sys.stderr.write(completed.stderr)
        raise SystemExit(completed.returncode)
    return completed.stdout


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    parser.add_argument("--cases", type=Path, help="case JSON (defaults to the checked-in corpus)")
    parser.add_argument(
        "--full-suite", action="store_true", help="run all gx-engine tests with the fresh oracle data"
    )
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[3]
    manifest = root / "src/gx-engine/Cargo.toml"
    oracle = root / "src/gx-engine/scripts/decimal-oracle/decimal-oracle.csproj"

    with tempfile.TemporaryDirectory(prefix="gx-engine-decimal-oracle-") as directory:
        temporary = Path(directory)
        if args.cases:
            cases = args.cases.resolve()
            if not cases.is_file():
                raise SystemExit(f"case file does not exist: {cases}")
        else:
            checked_in_cases = root / "src/gx-engine/scripts/decimal-oracle/cases.json"
            cases = temporary / "cases.json"
            corpus = json.loads(checked_in_cases.read_text(encoding="utf-8"))
            corpus.extend(generated_cases())
            cases.write_text(json.dumps(corpus), encoding="utf-8")

        # This is an independently authored .NET System.Decimal implementation,
        # not a GeneXus SDK probe or evidence of GeneXus semantic parity.
        output = run(
            [args.dotnet, "run", "--project", str(oracle), "--configuration", "Release", "--", str(cases)],
            cwd=root,
        )
        try:
            results = json.loads(output.strip())
        except json.JSONDecodeError as error:
            raise SystemExit(f".NET oracle did not emit JSON: {error}\n{output}") from error
        if not isinstance(results, list) or not results:
            raise SystemExit(".NET oracle returned no results")

        result_path = temporary / "oracle-results.json"
        result_path.write_text(json.dumps(results), encoding="utf-8")
        env = os.environ.copy()
        env["GX_ENGINE_DECIMAL_ORACLE_RESULTS"] = str(result_path)
        env["GX_ENGINE_DECIMAL_ORACLE_CASES"] = str(cases)
        test_command = ["cargo", "test", "--offline", "--locked", "--manifest-path", str(manifest)]
        if not args.full_suite:
            test_command.extend(
                [
                    "--test", "decimal_oracle", "--", "--exact",
                    "decimal_operations_match_independent_dotnet_oracle", "--include-ignored",
                ]
            )
        else:
            test_command.extend(["--", "--include-ignored"])
        run(test_command, cwd=root, env=env)
    print(f"Matched {len(results)} arithmetic cases against .NET System.Decimal.")
    print("This establishes only the covered .NET oracle subset, not GeneXus SDK parity.")


if __name__ == "__main__":
    main()
