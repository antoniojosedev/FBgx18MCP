using Xunit;

namespace GxMcp.Worker.Tests
{
    // xUnit parallelizes across test classes by default. Classes that share
    // process-global state (Console.Out / Console.Error redirection, static
    // SDK reflection probes, single-instance Logger singletons) collide when
    // their instances run concurrently. Each CollectionDefinition below pins
    // the listed classes into a single serial bucket — within the bucket
    // tests run one at a time; buckets still run in parallel with each other.

    [CollectionDefinition("StderrCapture", DisableParallelization = true)]
    public class StderrCaptureCollection { }

    // PatternApplyServiceTests was already tagged with this name; pinning the
    // dispatcher source-text assertion into the same bucket guards it from any
    // future test that mutates files under bin/ while it reads them.
    [CollectionDefinition("InProcessSdkReflection", DisableParallelization = true)]
    public class InProcessSdkReflectionCollection { }
}
