using Xunit;

namespace GxMcp.Gateway.Tests
{
    // AutoTypeInjector keeps its completion index in a shared static dictionary.
    // CompletionNameTests and AutoTypeInjectorTests both PrimeIndex/ClearAll it,
    // and xUnit runs test classes in parallel by default — a ClearAll/PrimeIndex
    // in one class could wipe the dict mid-assertion in the other (observed:
    // CompleteName_PrefixMatch expected 2, got 1). Both classes join this
    // collection so they run serially and never overlap.
    [CollectionDefinition("AutoTypeInjectorState", DisableParallelization = true)]
    public sealed class AutoTypeInjectorStateCollection { }
}
