# GeneXus SDK CI validation lane

Hosted CI remains the fast, credential-free contract lane. It builds and tests the
Gateway and records the Worker coverage skip when GeneXus is not installed. The
Worker must not be treated as passed on a hosted runner.

The executable SDK lane is a protected, opt-in GitHub Actions job on a Windows
self-hosted runner labelled `gx-sdk-18`. Enable it only with the repository
variable `GXMCP_SDK_CI_ENABLED=true`. The machine must already have a licensed
GeneXus 18 installation and a verified disposable synthetic KB; neither the SDK,
credentials, database dumps, nor licensed artifacts belong in this repository.

Required runner environment:

- `GXMCP_SDK_CI_LICENSE_ACK=1` — administrator acknowledgement that the runner's
  GeneXus installation is licensed for this validation use.
- `GXMCP_SDK_PATH` — absolute SDK installation path.
- `GXMCP_TEST_KB` — absolute path to the disposable KB.
- `GXMCP_TEST_FIXTURE` — attestation manifest matching that KB, as required by
  [`live-kb-test-harness.md`](live-kb-test-harness.md).

To serve one job from a licensed machine, run
`scripts/ci/start-sdk-runner.ps1 -RunnerDirectory <actions-runner dir> -KbPath <disposable KB> -FixturePath <manifest>`
(add `-PreflightOnly` to check without registering). Before it registers an ephemeral `gx-sdk-18` runner and sets
the variables above, it checks the three things that failed the lane on a real runner:

- the manifest matches the KB on disk. A GeneXus open can rewrite the `.gxw`, so a manifest goes stale after a live
  run; pick the one for the current revision or regenerate it with `scripts/new-live-fixture-manifest.ps1`;
- a port in 55100..55199 can be bound. Windows can reserve that range with nothing listening on it
  (`netsh int ipv4 show excludedportrange protocol=tcp`);
- the .NET SDK for the job is already in `<runner>\dotnet-ci`, fetched with `curl.exe`. `setup-dotnet` installs into
  `DOTNET_INSTALL_DIR`, the runner user usually cannot write `C:\Program Files\dotnet`, and its PowerShell download
  of the SDK zip can stall for half an hour.

The job runs `scripts/ci/sdk-validation.ps1`. It first runs
`scripts/validate-gx-sdk.ps1` against `config/sdk-compatibility.json`, then compares
the real Release rebuild warning baseline and runs the existing live Gateway/Worker validation. The script creates an
ignored run directory and isolated config, restores process environment variables,
and removes the directory in `finally`; it never copies or publishes SDK bytes.

The lane writes `status.json` and `sdk-validation.log` under the runner temp
artifact directory. Status is explicit:

- `pass`: fingerprint, Release warning baseline, and live Worker validation completed.
- `skip`: a required licensed-machine or fixture precondition is unavailable.
- `fail`: a precondition or validation failed; the job fails.

Status schema v2 includes `commit`, the exact checked-out SHA. The required
`test` job depends on every critical lane, including `rust-engine`. For a backend
routing/default change, it accepts SDK evidence only when the SDK job succeeds,
reports `state=pass`, and its commit equals the run's `GITHUB_SHA`. Disabled,
skipped, missing, or stale evidence blocks the gate. Local logs alone are not a
remote CI attestation.

The SDK job never executes fork pull-request code. A maintainer must review a
fork change and place the trusted candidate on a branch in this repository
before the protected runner can validate it. With no registered licensed runner,
leave the lane disabled and report the release blocked; never post a synthetic
passing status or relax the required check to publish.

The workflow uploads the status and log with retention, and writes the skip reason
to the Actions job summary. A disabled lane is visible as `skipped` rather than
silently implying Worker coverage.
