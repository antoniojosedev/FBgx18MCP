using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Behaviour of the independent re-read behind <c>ObjectService.FindObjectFreshByIdentity</c>.
    /// Issue #733: the SDK keeps UserControl, Image, ThemeClass and DesignSystem resident, so
    /// invalidating the cache entry left the lookup returning the very instance being verified,
    /// and every property write, preview and reconciliation read of those types answered
    /// FreshReadUnavailable.
    /// </summary>
    public class FreshObjectReloadTests
    {
        private sealed class Instance
        {
            public Guid Id;
            public string Tag;
        }

        // The model of a resident type: Invalidate leaves the instance where it is, and only
        // evicting it makes the next lookup build a new one. The other types need no eviction.
        private sealed class ResidentModel
        {
            public readonly Instance Resident;
            public readonly List<string> Calls = new List<string>();
            public bool Evicted;

            public ResidentModel(Guid id) { Resident = new Instance { Id = id, Tag = "resident" }; }

            public bool Invalidate(Instance instance) { Calls.Add("invalidate"); return true; }
            public void Evict(Instance instance) { Calls.Add("evict"); Evicted = true; }
            public Instance Resolve()
            {
                Calls.Add("resolve");
                return Evicted ? new Instance { Id = Resident.Id, Tag = "reloaded" } : Resident;
            }
        }

        private static Instance Reload(ResidentModel model, Action<Instance> release, out FreshReloadOutcome outcome)
            => FreshObjectReload.Reload(model.Resident, i => i.Id, model.Invalidate, release, model.Resolve, out outcome);

        [Fact]
        public void ResidentInstance_IsReloadedBecauseTheRetainedInstanceIsEvictedBeforeTheLookup()
        {
            var model = new ResidentModel(Guid.NewGuid());

            Instance fresh = Reload(model, model.Evict, out FreshReloadOutcome outcome);

            Assert.Equal(FreshReloadOutcome.Independent, outcome);
            Assert.NotNull(fresh);
            Assert.NotSame(model.Resident, fresh);
            Assert.Equal("reloaded", fresh.Tag);
            Assert.Equal(model.Resident.Id, fresh.Id);
        }

        [Fact]
        public void WithoutEviction_AResidentInstanceIsRefusedAsTheSameInstance()
        {
            // What FindObjectFreshByIdentity did before the fix: invalidate, then look up.
            var model = new ResidentModel(Guid.NewGuid());

            Instance fresh = Reload(model, instance => { }, out FreshReloadOutcome outcome);

            Assert.Null(fresh);
            Assert.Equal(FreshReloadOutcome.SameInstanceRetained, outcome);
        }

        [Fact]
        public void Invalidate_Release_Resolve_RunInThatOrder()
        {
            var model = new ResidentModel(Guid.NewGuid());

            Reload(model, model.Evict, out _);

            Assert.Equal(new[] { "invalidate", "evict", "resolve" }, model.Calls);
        }

        [Fact]
        public void WhenTheCacheCannotBeInvalidated_NothingIsReleasedOrResolved()
        {
            var model = new ResidentModel(Guid.NewGuid());

            Instance fresh = FreshObjectReload.Reload(model.Resident, i => i.Id,
                instance => { model.Calls.Add("invalidate"); return false; }, model.Evict, model.Resolve,
                out FreshReloadOutcome outcome);

            Assert.Null(fresh);
            Assert.Equal(FreshReloadOutcome.InvalidationUnavailable, outcome);
            Assert.Equal(new[] { "invalidate" }, model.Calls);
        }

        [Fact]
        public void AReleaseThatThrows_DoesNotStopTheLookup()
        {
            var expected = new Instance { Id = Guid.NewGuid(), Tag = "expected" };
            var other = new Instance { Id = expected.Id, Tag = "other" };

            Instance fresh = FreshObjectReload.Reload(expected, i => i.Id, i => true,
                i => throw new InvalidOperationException("the SDK has no eviction"), () => other,
                out FreshReloadOutcome outcome);

            Assert.Same(other, fresh);
            Assert.Equal(FreshReloadOutcome.Independent, outcome);
        }

        [Fact]
        public void AReleaseThatThrows_StillRefusesAnInstanceThatIsNotIndependent()
        {
            var expected = new Instance { Id = Guid.NewGuid(), Tag = "expected" };

            Instance fresh = FreshObjectReload.Reload(expected, i => i.Id, i => true,
                i => throw new InvalidOperationException("the SDK has no eviction"), () => expected,
                out FreshReloadOutcome outcome);

            Assert.Null(fresh);
            Assert.Equal(FreshReloadOutcome.SameInstanceRetained, outcome);
        }

        [Fact]
        public void ALookupThatFindsNothing_IsNotResolved()
        {
            var expected = new Instance { Id = Guid.NewGuid() };

            Instance fresh = FreshObjectReload.Reload(expected, i => i.Id, i => true, i => { }, () => null,
                out FreshReloadOutcome outcome);

            Assert.Null(fresh);
            Assert.Equal(FreshReloadOutcome.NotResolved, outcome);
        }

        [Fact]
        public void ALookupThatReturnsAnotherObject_IsADifferentObject()
        {
            var expected = new Instance { Id = Guid.NewGuid() };
            var another = new Instance { Id = Guid.NewGuid() };

            Instance fresh = FreshObjectReload.Reload(expected, i => i.Id, i => true, i => { }, () => another,
                out FreshReloadOutcome outcome);

            Assert.Null(fresh);
            Assert.Equal(FreshReloadOutcome.DifferentObject, outcome);
        }

        // The outcome is named, not typed: it is internal, and a public test method cannot take it.
        [Theory]
        [InlineData("InvalidationUnavailable", "InvalidationUnavailable")]
        [InlineData("NotResolved", "IdentityNotResolved")]
        [InlineData("DifferentObject", "IdentityMismatch")]
        [InlineData("SameInstanceRetained", "SameInstanceRetained")]
        public void EveryRefusalHasItsOwnReasonAndStaysFreshReadUnavailable(string outcomeName, string reason)
        {
            var outcome = (FreshReloadOutcome)Enum.Parse(typeof(FreshReloadOutcome), outcomeName);
            JObject diagnostic = FreshObjectReload.Describe(outcome, "UserControl");

            Assert.Equal("FreshReadUnavailable", diagnostic["code"]?.ToString());
            Assert.Equal(reason, diagnostic["reason"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(diagnostic["message"]?.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(diagnostic["hint"]?.ToString()));
            Assert.DoesNotContain("\n", diagnostic["message"].ToString());
        }

        [Fact]
        public void TheRetainedInstanceDiagnosticNamesTheTypeAndTheCause()
        {
            string message = FreshObjectReload.Describe(FreshReloadOutcome.SameInstanceRetained, "UserControl")["message"].ToString();

            Assert.Contains("UserControl", message);
            Assert.Contains("same in-memory", message);
            Assert.Contains("evicted", message);
        }

        [Fact]
        public void TheMessagesTheCallersAlreadyShowAreKept()
        {
            Assert.Equal("The SDK object cache could not be invalidated; a verification read was not attempted.",
                FreshObjectReload.Describe(FreshReloadOutcome.InvalidationUnavailable, null)["message"].ToString());
        }
    }
}
