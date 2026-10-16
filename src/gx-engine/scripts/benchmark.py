#!/usr/bin/env python3
"""Benchmark gx-engine capabilities against the real GeneXus runtime.

For every shared capability the runner executes two implementations over the
same recorded corpus - the native side through the GxClasses assembly that
generated applications link against (`scripts/sdk-oracle`), the engine side
through its Rust bench target - and compares live outputs (row count, character
count and an FNV-1a/UTF-16 hash) as well as per-pass timings. Reports are
measurements written to the ignored scratchpad, not committed evidence.
"""

from __future__ import annotations

import argparse
import json
import platform
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
CORPUS = ROOT / "src/gx-engine/tests/fixtures/str_matrix.tsv"


def run(command: list[str], *, cwd: Path, allow_exit: tuple[int, ...] = ()) -> str:
    completed = subprocess.run(
        command,
        cwd=cwd,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
    )
    if completed.returncode and completed.returncode not in allow_exit:
        sys.stderr.write(completed.stdout)
        sys.stderr.write(completed.stderr)
        raise SystemExit(completed.returncode)
    return completed.stdout


def tool_version(*command: str) -> str:
    try:
        completed = subprocess.run(command, text=True, capture_output=True, check=False)
    except OSError:
        return "unavailable"
    first = completed.stdout.strip().splitlines()
    return first[0] if first else "unknown"


def last_json_line(output: str) -> dict:
    for line in reversed(output.splitlines()):
        line = line.strip()
        if line.startswith("{"):
            return json.loads(line)
    raise SystemExit(f"no JSON report found in output:\n{output}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--iterations",
        type=int,
        default=50,
        help="measured passes per side (default 50)",
    )
    parser.add_argument(
        "--corpus",
        type=Path,
        default=CORPUS,
        help=f"corpus TSV (default {CORPUS.relative_to(ROOT)})",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=ROOT / "scratchpad/engine-benchmark.json",
        help="report path (default scratchpad/engine-benchmark.json)",
    )
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    args = parser.parse_args()

    corpus = args.corpus.resolve()
    if not corpus.is_file():
        raise SystemExit(f"corpus does not exist: {corpus}")
    if args.iterations <= 0:
        raise SystemExit("--iterations must be positive")

    manifest = ROOT / "src/gx-engine/Cargo.toml"
    oracle = ROOT / "src/gx-engine/scripts/sdk-oracle/sdk-oracle.csproj"

    # Exit code 1 from either side reports corpus mismatches; the JSON parity
    # fields below carry the detail, so keep the output instead of aborting.
    native = last_json_line(
        run(
            [
                args.dotnet,
                "run",
                "--project",
                str(oracle),
                "--configuration",
                "Release",
                "--",
                "bench-str",
                str(corpus),
                str(args.iterations),
            ],
            cwd=ROOT,
            allow_exit=(1,),
        )
    )
    engine = last_json_line(
        run(
            [
                "cargo",
                "bench",
                "--offline",
                "--locked",
                "--manifest-path",
                str(manifest),
                "--bench",
                "str_format",
                "--",
                str(corpus),
                str(args.iterations),
            ],
            cwd=ROOT,
            allow_exit=(1,),
        )
    )

    parity = {
        "rows_match": native["rows"] == engine["rows"],
        "chars_match": native["chars"] == engine["chars"],
        "hash_match": native["hash"] == engine["hash"],
        "native_expected_mismatches": native["expected_mismatches"],
        "engine_expected_mismatches": engine["expected_mismatches"],
        "engine_errors": engine["errors"],
    }
    parity_ok = (
        parity["rows_match"]
        and parity["chars_match"]
        and parity["hash_match"]
        and parity["native_expected_mismatches"] == 0
        and parity["engine_expected_mismatches"] == 0
        and parity["engine_errors"] == 0
    )
    ratio = native["median_ms"] / engine["median_ms"] if engine["median_ms"] else 0.0

    report = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "capability": "str_format",
        "corpus": {"path": str(corpus.relative_to(ROOT)), "rows": engine["rows"]},
        "iterations": args.iterations,
        "environment": {
            "platform": platform.platform(),
            "processor": platform.processor(),
            "rustc": tool_version("rustc", "-V"),
            "dotnet": tool_version(args.dotnet, "--version"),
        },
        "native": native,
        "engine": engine,
        "parity": parity,
        "speed": {
            "native_median_ms": native["median_ms"],
            "engine_median_ms": engine["median_ms"],
            "native_over_engine": ratio,
            "native_ns_per_row": native["ns_per_row"],
            "engine_ns_per_row": engine["ns_per_row"],
        },
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    print("== gx-engine benchmark: str_format ==")
    print(
        f"corpus {report['corpus']['path']} ({engine['rows']} rows), "
        f"{args.iterations} passes (+1 warm-up)"
    )
    print(
        f"native {native['impl']} {native['version']}: "
        f"median {native['median_ms']:.2f} ms/pass, {native['ns_per_row']:.0f} ns/row"
    )
    print(
        f"engine {engine['impl']} {engine['version']}: "
        f"median {engine['median_ms']:.2f} ms/pass, {engine['ns_per_row']:.0f} ns/row"
    )
    print(
        "parity: "
        f"rows {'ok' if parity['rows_match'] else 'FAIL'}, "
        f"chars {'ok' if parity['chars_match'] else 'FAIL'}, "
        f"hash {'ok' if parity['hash_match'] else 'FAIL'}; "
        "expected-text mismatches "
        f"native {native['expected_mismatches']} / engine {engine['expected_mismatches']}"
    )
    print(
        f"speed: engine {ratio:.2f}x native throughput"
        if ratio >= 1
        else f"speed: engine {ratio:.2f}x of native throughput"
    )
    print(f"report: {args.out}")
    if not parity_ok:
        raise SystemExit("PARITY FAILURE - see report")


if __name__ == "__main__":
    main()
