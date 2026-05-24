using Xunit;

// v2.6.9 — Worker tests share enough process-global state (Console.Error
// redirection in LoggerPhaseTagTests, the SDK's static type cache touched
// by PatternApplyServiceTests, the InProcessBuildEngine adapter, etc.) that
// cross-collection parallel execution intermittently surfaces a NRE in
// PatternApplyService.ApplyPatternToObject — different test classes racing
// on Console.Error swap during a Logger.Info call could leave a stale
// writer reference even though Logger wraps the write in try/catch (the
// NRE bubbles before the catch on some JIT paths). Pinning the whole
// assembly to serial execution adds ~5s to the suite (7s -> 12s) in
// exchange for deterministic green; xunit collection sharding alone was
// not enough because collections still run in parallel with each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace GxMcp.Worker.Tests
{
    // The collections below remain because they document intent at the class
    // level: with assembly-wide DisableTestParallelization the
    // DisableParallelization flags on the collections themselves are a no-op,
    // but the [Collection(...)] tags on the classes still group ownership.

    [CollectionDefinition("StderrCapture", DisableParallelization = true)]
    public class StderrCaptureCollection { }

    [CollectionDefinition("InProcessSdkReflection", DisableParallelization = true)]
    public class InProcessSdkReflectionCollection { }
}
