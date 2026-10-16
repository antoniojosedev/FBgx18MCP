"""Live regression: object_atomic must type SDT variables before source validation.

Before the fix, `genexus_create action=object_atomic` bound SDT variables by name,
so a Procedure created in the same call that read `&Var.Member` failed with
src0216 ("'Amount' invalid property") and was rolled back.

Runs only against a disposable KB whose folder starts with C:\\KBs\\KBTeste
(registered as alias `live-fixture`). It creates a uniquely named SDT and
Procedure and leaves them in that KB. Uses the published gateway (run build.ps1
first).

    python scripts/tests/live-sdt-member-access.py
"""
import importlib.util
import json
import socket
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("wire", ROOT / "scripts" / "mcp-wire-conformance.py")
wire = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wire)

SOURCE = "\n".join([
    "&First = new()",
    "&First.Amount = 7",
    "&Second = &First.Clone()",
    "&Second.Amount = 9",
    'msg("first=" + &First.Amount.ToString().Trim(), status)',
])


def main() -> int:
    run = ROOT / "scratchpad" / ("live-sdt-member-access-" + str(time.time_ns()))
    run.mkdir(parents=True)
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    process, out, err = wire.start_gateway(run, port, False)
    try:
        wire.wait_port(port, process)
        headers = wire.legacy_headers()

        def rpc(method, params=None):
            body = {"jsonrpc": "2.0", "id": time.time_ns() % 1_000_000_000, "method": method, "params": params or {}}
            status, payload, response_headers = wire.post_http(port, body, headers, timeout=180)
            if status != 200 or payload.get("error"):
                raise RuntimeError((status, payload))
            return payload, response_headers

        _, h = rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "live-sdt-member-access", "version": "1"}})
        headers = wire.legacy_headers(next(v for k, v in h.items() if k.lower() == "mcp-session-id"))
        wire.post_http(port, {"jsonrpc": "2.0", "method": "notifications/initialized"}, headers)

        def call(name, args):
            payload, _ = rpc("tools/call", {"name": name, "arguments": args})
            (run / f"{name}-{time.time_ns()}.json").write_text(json.dumps(payload, indent=2), encoding="utf-8")
            result = payload.get("result", {})
            if result.get("structuredContent") is not None:
                return result["structuredContent"], bool(result.get("isError"))
            text = (result.get("content") or [{}])[0].get("text", "{}")
            return json.loads(text), bool(result.get("isError"))

        selected, _ = call("genexus_kb", {"action": "select", "alias": "live-fixture"})
        kb_path = str(selected.get("path", "")).replace("\\", "/").lower()
        if not kb_path.startswith("c:/kbs/kbteste"):
            raise RuntimeError("refusing to mutate non-disposable KB: " + kb_path)
        call("genexus_lifecycle", {"action": "status", "wait": 10})

        suffix = str(time.time_ns())[-8:]
        sdt_name, proc_name = "LiveSdtMember" + suffix, "LiveSdtMemberProc" + suffix
        invalid_name = "LiveSdtInvalid" + suffix
        invalid, failed = call("genexus_create", {
            "action": "object_atomic", "type": "Procedure", "name": invalid_name,
            "mode": "create", "source": "&Value = 1\\nPrint(&Value)", "validate": False,
        })
        wire.assert_true(failed and invalid.get("code") == "AtomicCreateValidationFailed", "literal escape was not rejected before saving")
        wire.assert_true(any(e.get("field") == "source" for e in invalid.get("fieldErrors", [])), "terse error lost source fieldErrors")
        missing, failed = call("genexus_read", {"name": invalid_name, "type": "Procedure", "part": "Source", "refresh": True})
        wire.assert_true(failed and missing.get("code") == "ObjectNotFound", "invalid object unexpectedly persisted")

        sdt, failed = call("genexus_create", {"action": "object", "type": "SDT", "name": sdt_name, "firstItem": "Amount", "firstItemType": "Numeric(4.0)"})
        wire.assert_true(not failed, "SDT create failed: " + json.dumps(sdt)[:600])

        proc, failed = call("genexus_create", {
            "action": "object_atomic", "type": "Procedure", "name": proc_name, "mode": "create",
            "variables": [{"varName": "First", "typeName": sdt_name}, {"varName": "Second", "typeName": sdt_name}],
            "source": SOURCE, "validate": False,
        })
        wire.assert_true(not failed and proc.get("code") == "ObjectCreatedAtomic",
                         "object_atomic with SDT member access failed: " + json.dumps(proc)[:800])

        variables, failed = call("genexus_read", {"name": proc_name, "type": "Procedure", "part": "Variables", "refresh": True})
        wire.assert_true(not failed, "Variables readback failed")
        text = variables.get("source", "")
        for var in ("&First", "&Second"):
            wire.assert_true(f"{var} : {sdt_name}" in text, f"{var} not typed as {sdt_name}: {text[:300]}")
        source, failed = call("genexus_read", {"name": proc_name, "type": "Procedure", "part": "Source", "refresh": True})
        wire.assert_true(not failed and not source.get("truncated") and not source.get("isTruncatedByWorker"), "Source readback failed or was truncated")
        actual_source = source.get("source", "").replace("\r\n", "\n").strip()
        wire.assert_true(actual_source == SOURCE, "Source readback differs from authored member access")
        print(f"PASS {proc_name} uses {sdt_name}; source/variables read back; evidence {run}")
        return 0
    finally:
        wire.stop_process(process)
        out.close()
        err.close()


if __name__ == "__main__":
    sys.exit(main())
