using Xunit;
using GxMcp.Worker.Compatibility;

namespace GxMcp.Worker.Tests
{
    // Issue #477: the receipt and dryRun plan both claimed "functional behavior
    // and pattern bindings are preserved" after the native SDK materialized
    // page="<default>" into a concrete value. That is the opposite of what
    // happens to the attribute - GeneXus then treats it as an explicit
    // instance-level override that no longer follows the pattern template
    // default - so an agent reading the receipt reasoned about future default
    // changes from a false premise.
    public class WorkWithDefaultNormalizationDisclosureTests
    {
        [Fact]
        public void NativeDisclosure_StatesTheOverrideConsequence()
        {
            var text = WorkWithSdkAdapter.NativeDefaultNormalization;

            Assert.Contains("no longer follow a later change", text);
            Assert.Contains("explicit instance override", text);
        }

        [Fact]
        public void NativeDisclosure_DoesNotPromisePreservedBindings()
        {
            // The exact overclaim that shipped. Guarded so a future edit that
            // reintroduces it fails here rather than in an agent's plan.
            Assert.DoesNotContain(
                "pattern bindings are preserved",
                WorkWithSdkAdapter.NativeDefaultNormalization);
        }

        [Fact]
        public void FallbackDisclosure_SaysDefaultsAreUntouched()
        {
            Assert.Contains("verbatim", WorkWithSdkAdapter.FallbackDefaultNormalization);
        }

        [Fact]
        public void BothRoutes_DeclareNormalization()
        {
            // The two must not collapse into the same sentence: only the native
            // route materializes, and saying otherwise would send an agent
            // looking for drift that cannot exist on the fallback route.
            Assert.NotEqual(
                WorkWithSdkAdapter.NativeDefaultNormalization,
                WorkWithSdkAdapter.FallbackDefaultNormalization);
        }
    }
}
