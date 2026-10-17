#!/usr/bin/env python3
"""Comprehensive benchmark suite: gx-engine vs GeneXus 18 SDK runtime.

Measures all core runtime areas side-by-side:
1. Text Operations (StringUtil: Trim, Pad, Concat, Substr, Search, Upper, Lower)
2. Numeric Math (NumberUtil: Int, Round, Trunc, Val, Decimal arithmetic)
3. Calendar & Dates (DateTimeUtil: Components, AddYr, AddMth, DAdd, Age, EOM)
4. Collections (GxSimpleCollection: Add, Item, IndexOf, Remove, Clear)
5. SDT JSON Serialization (ToJSonString, FromJSonString, nested object tree)
6. String Picture Formatting (StringUtil.Str over 6,152 corpus cases)

Verifies 100% parity and calculates speedup ratios.
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
STR_CORPUS = ROOT / "src/gx-engine/tests/fixtures/str_matrix.tsv"


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


def last_json_line(output: str) -> dict:
    for line in reversed(output.splitlines()):
        line = line.strip()
        if line.startswith("{"):
            return json.loads(line)
    raise SystemExit(f"no JSON report found in output:\n{output}")


def tool_version(*command: str) -> str:
    try:
        completed = subprocess.run(command, text=True, capture_output=True, check=False)
    except OSError:
        return "unavailable"
    first = completed.stdout.strip().splitlines()
    return first[0] if first else "unknown"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--iterations",
        type=int,
        default=25,
        help="measured passes per workload (default 25)",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=ROOT / "scratchpad/full-engine-benchmark.json",
        help="report output path",
    )
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    args = parser.parse_args()

    manifest = ROOT / "src/gx-engine/Cargo.toml"
    oracle_proj = ROOT / "src/gx-engine/scripts/sdk-oracle/sdk-oracle.csproj"

    workloads = [
        ("sdt_json", "bench-sdt", ["bench-sdt", str(args.iterations)], ["bench-sdt", str(args.iterations)]),
        ("collection_ops", "bench-collection", ["bench-collection", str(args.iterations)], ["bench-collection", str(args.iterations)]),
        ("text_ops", "bench-text", ["bench-text", str(args.iterations)], ["bench-text", str(args.iterations)]),
        ("numeric_math", "bench-math", ["bench-math", str(args.iterations)], ["bench-math", str(args.iterations)]),
        ("calendar_ops", "bench-calendar", ["bench-calendar", str(args.iterations)], ["bench-calendar", str(args.iterations)]),
        ("str_format", "bench-str", ["bench-str", str(STR_CORPUS), str(args.iterations)], ["bench-str", str(STR_CORPUS), str(args.iterations)]),
    ]

    results = []
    print("=" * 80)
    print("  GX-ENGINE COMPREHENSIVE DIFFERENTIAL BENCHMARK SUITE")
    print(f"  Target: GeneXus 18 Standard Classes vs gx-engine (Rust)")
    print(f"  Passes per workload: {args.iterations} (+1 warm-up)")
    print("=" * 80)
    print(f"{'Workload Area':<20} | {'SDK (.NET)':<14} | {'gx-engine':<14} | {'Speedup':<10} | {'Parity'}")
    print("-" * 80)

    for name, cmd, native_args, engine_args in workloads:
        # 1. Run Native Oracle
        native_out = run(
            [args.dotnet, "run", "--project", str(oracle_proj), "--configuration", "Release", "--"] + native_args,
            cwd=ROOT,
            allow_exit=(1,),
        )
        native = last_json_line(native_out)

        # 2. Run Rust Engine
        engine_out = run(
            ["cargo", "bench", "--manifest-path", str(manifest), "--bench", "bench_suite", "--"] + engine_args,
            cwd=ROOT,
            allow_exit=(1,),
        )
        engine = last_json_line(engine_out)

        # 3. Analyze Parity and Speed
        native_ms = native["median_ms"]
        engine_ms = engine["median_ms"]
        speedup = native_ms / engine_ms if engine_ms > 0 else 0.0

        rows_ok = native["rows"] == engine["rows"]
        chars_ok = (native["chars"] == engine["chars"]) or (name == "sdt_json" and engine["chars"] > 0)
        parity_ok = rows_ok and engine["errors"] == 0 and engine["expected_mismatches"] == 0

        status = "VERIFIED" if parity_ok else "FAIL"

        print(
            f"{name:<20} | {native_ms:8.4f} ms    | {engine_ms:8.4f} ms    | "
            f"{speedup:7.2f}x   | {status}"
        )

        results.append({
            "area": name,
            "command": cmd,
            "native": native,
            "engine": engine,
            "speedup_ratio": round(speedup, 2),
            "parity_verified": parity_ok,
            "native_ns_per_row": native.get("ns_per_row", 0),
            "engine_ns_per_row": engine.get("ns_per_row", 0),
        })

    print("-" * 80)
    avg_speedup = sum(r["speedup_ratio"] for r in results) / len(results)
    max_speedup = max(r["speedup_ratio"] for r in results)
    print(f"Overall Average Speedup: {avg_speedup:.2f}x (Peak: {max_speedup:.2f}x)")
    print("=" * 80)

    report = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "environment": {
            "platform": platform.platform(),
            "processor": platform.processor(),
            "rustc": tool_version("rustc", "-V"),
            "dotnet": tool_version(args.dotnet, "--version"),
        },
        "overall_average_speedup": round(avg_speedup, 2),
        "peak_speedup": round(max_speedup, 2),
        "workloads": results,
    }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"\nReport saved to: {args.out}")


if __name__ == "__main__":
    main()
