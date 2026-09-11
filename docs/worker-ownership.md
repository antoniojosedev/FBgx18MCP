# Worker ownership and orphan cleanup

`WorkerProcess` owns a checkout-scoped lease for each spawned worker. The lease key is a SHA-256 digest of the normalized worker executable path and KB path, so two checkouts serving the same KB do not share cleanup records. A named Windows mutex serializes reservation writes across gateway processes.

The registry stores the owning gateway PID, worker PID, and the worker's UTC start time. Starting a worker with an existing live lease fails closed; it never enumerates or kills unrelated processes. When the gateway exits cleanly, `StopProcess` releases the record before notifying the pool so a replacement can reserve it. If a gateway dies, the next reservation may reap only the recorded worker when both PID and start time still match, preventing PID-reuse cleanup.

A health-check tick performs a once-per-minute reconciliation against this worker's exact registry record. This is a narrow legacy/orphan fallback, not a system-wide process or WMI scan. The old per-start `KillOrphanWorkers` sweep was removed. The existing command-line/WMI lookup used for the exit-code-17 diagnostic remains separate and is not a cleanup mechanism.

This design intentionally scopes ownership by executable path as well as KB path. A debug worker from checkout A and a debug worker from checkout B therefore have independent records and cannot reap one another. Operators should remove stale files under `%LOCALAPPDATA%\GxMcp\worker-ownership` only when no corresponding gateway is running; normal recovery handles stale records automatically.
