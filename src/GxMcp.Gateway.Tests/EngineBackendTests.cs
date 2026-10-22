using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Guards the experimental engine backend seam (docs/engine_backend.md):
    /// settings resolution, the capability gate, executable discovery, the
    /// --version handshake and its genexus_doctor reporting.
    ///
    /// Every test here that mutates GXMCP_ENGINE_* lives in this one class on
    /// purpose: xunit runs classes in parallel and tests within a class
    /// sequentially, so environment mutation stays race-free.
    /// </summary>
    public class EngineBackendTests : EngineEnvironmentTestBase
    {
        // ---------------------------------------------------------------- settings

        [Fact]
        public void Settings_DefaultToGxEngineBackend_WithNoConfigAndNoEnvironment()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, null);
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, null);

            var settings = EngineBackendSettings.Resolve(null);

            Assert.Equal("gxengine", settings.Backend);
            Assert.Equal("sdk", settings.Fallback);
            Assert.Null(settings.Executable);
            Assert.Equal(EngineBackendSettings.DefaultTimeoutSeconds, settings.TimeoutSeconds);
            Assert.True(settings.EngineEnabled);
            Assert.False(settings.RejectUnsupported);
        }

        [Fact]
        public void Settings_ReadTheConfigEngineSection()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, null);
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, null);

            var config = new Configuration
            {
                Engine = new EngineConfig
                {
                    Backend = " gxengine ",
                    Fallback = "error",
                    Executable = @"C:\tools\gx-engine.exe",
                    TimeoutSeconds = 45
                }
            };

            var settings = EngineBackendSettings.Resolve(config);

            Assert.Equal("gxengine", settings.Backend);
            Assert.Equal("error", settings.Fallback);
            Assert.Equal(@"C:\tools\gx-engine.exe", settings.Executable);
            Assert.Equal(45, settings.TimeoutSeconds);
            Assert.True(settings.EngineEnabled);
            Assert.True(settings.RejectUnsupported);
        }

        [Fact]
        public void Settings_EnvironmentOverridesTheConfig()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, "gxengine");
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, @"C:\env\gx-engine.exe");
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, "33");

            var config = new Configuration
            {
                Engine = new EngineConfig
                {
                    Backend = "sdk",
                    Fallback = "sdk",
                    Executable = @"C:\config\gx-engine.exe",
                    TimeoutSeconds = 7
                }
            };

            var settings = EngineBackendSettings.Resolve(config);

            Assert.Equal("gxengine", settings.Backend);
            Assert.Equal("error", settings.Fallback);
            Assert.Equal(@"C:\env\gx-engine.exe", settings.Executable);
            Assert.Equal(33, settings.TimeoutSeconds);
        }

        [Fact]
        public void Settings_InvalidValues_KeepTheSdkDefaults()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, "claude");
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "yolo");
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, "later");

            var config = new Configuration
            {
                Engine = new EngineConfig { Backend = "also-invalid", Fallback = "also-invalid", TimeoutSeconds = -5 }
            };

            var settings = EngineBackendSettings.Resolve(config);

            Assert.Equal("sdk", settings.Backend);
            Assert.Equal("sdk", settings.Fallback);
            Assert.Equal(EngineBackendSettings.DefaultTimeoutSeconds, settings.TimeoutSeconds);
        }

        // ------------------------------------------------------------- router gate

        [Theory]
        [InlineData("parallel_rebuild_all", "Engine")]
        [InlineData("PARALLEL_REBUILD_ALL", "Engine")]
        [InlineData("udm_storage_access", "Sdk")]
        [InlineData("udm_type_query", "Sdk")]
        [InlineData("str_format", "Sdk")]
        [InlineData("numeric_core", "Sdk")]
        [InlineData("unknown_capability", "Sdk")]
        public void Router_Auto_OnlyAdmitsTheBuildCapability(string capabilityId, string expected)
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, "auto");
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);

            var settings = EngineBackendSettings.Resolve(null);

            Assert.Equal(expected, EngineRouter.Decide(capabilityId, settings).Target.ToString());
        }

        [Theory]
        [InlineData("parallel_rebuild_all", "Engine")]
        [InlineData("udm_type_query", "Engine")]
        [InlineData("udm_storage_access", "Sdk")]
        [InlineData("csharp_emitter", "Sdk")]
        [InlineData("unknown_capability", "Sdk")]
        public void Router_DefaultGxEngine_AdmitsOnlyTheBuildAndTheTypeQueryCapabilities(string capabilityId, string expected)
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);

            var settings = EngineBackendSettings.Resolve(null);

            Assert.Equal(expected, EngineRouter.Decide(capabilityId, settings).Target.ToString());
        }

        [Theory]
        [InlineData("str_format")]
        [InlineData("numeric_core")]
        [InlineData("native_kb_read")]
        [InlineData("unknown_capability")]
        public void Router_WithSdkBackend_AlwaysServesTheSdk(string capabilityId)
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);

            var sdkSettings = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "sdk", Fallback = "error" }
            });

            var decision = EngineRouter.Decide(capabilityId, sdkSettings);

            Assert.Equal(EngineRouteTarget.Sdk, decision.Target);
            Assert.Null(decision.Code);
        }

        [Fact]
        public void Router_ParityCapability_IsServedByTheEngine()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);

            var settings = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "gxengine" }
            });

            var decision = EngineRouter.Decide("str_format", settings);

            Assert.Equal(EngineRouteTarget.Engine, decision.Target);
            Assert.Equal("str_format", decision.CapabilityId);
            Assert.Null(decision.Code);
        }

        /// <summary>
        /// The storage record and the library modules that used to be labelled Parity
        /// without a native oracle must never be routable: not under the default
        /// policy, and refused (not silently served) under Fallback=error.
        /// </summary>
        [Theory]
        [InlineData("udm_storage_access")]
        [InlineData("native_kb_read")]
        [InlineData("sql_navigation")]
        [InlineData("csharp_emitter")]
        [InlineData("numeric_core")]
        [InlineData("general_gx_surface")]
        public void Router_DowngradedCapability_IsNotServedByTheEngine(string capabilityId)
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);
            var sdkFallback = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "gxengine", Fallback = "sdk" }
            });
            Assert.Equal(EngineRouteTarget.Sdk, EngineRouter.Decide(capabilityId, sdkFallback).Target);

            var errorFallback = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "gxengine", Fallback = "error" }
            });
            var decision = EngineRouter.Decide(capabilityId, errorFallback);
            Assert.Equal(EngineRouteTarget.Reject, decision.Target);
            Assert.Equal(EngineRouter.NotParityCode, decision.Code);
        }

        [Fact]
        public void Router_NonParityCapability_FallsBackToSdk_WhenFallbackIsSdk()
        {
            var settings = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "gxengine", Fallback = "sdk" }
            });

            var decision = EngineRouter.Decide("unregistered_capability", settings);

            Assert.Equal(EngineRouteTarget.Sdk, decision.Target);
            Assert.Null(decision.Code);
        }

        [Theory]
        [InlineData("unregistered_capability")]
        [InlineData("unknown_capability")]
        public void Router_NonParityCapability_IsRejected_WhenFallbackIsError(string capabilityId)
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");

            var settings = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "gxengine" }
            });

            var decision = EngineRouter.Decide(capabilityId, settings);

            Assert.Equal(EngineRouteTarget.Reject, decision.Target);
            Assert.Equal(EngineRouter.NotParityCode, decision.Code);
            Assert.False(string.IsNullOrWhiteSpace(decision.Message));
        }

        // --------------------------------------------------------- registry rules

        [Fact]
        public void Registry_IdsAreUniqueAndWellFormed()
        {
            var ids = EngineCapabilityRegistry.All.Select(c => c.Id).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var capability in EngineCapabilityRegistry.All)
            {
                Assert.Matches(new Regex("^[a-z][a-z0-9_]*$"), capability.Id);
                Assert.False(string.IsNullOrWhiteSpace(capability.Title), capability.Id);
                Assert.False(string.IsNullOrWhiteSpace(capability.EngineSurface), capability.Id);
                Assert.False(string.IsNullOrWhiteSpace(capability.Notes), capability.Id);
            }
        }

        /// <summary>
        /// A status is a claim with evidence: every capability must cite at least
        /// one repository path, and that path must exist. This is what stops the
        /// registry from degrading into an optimistic list.
        /// </summary>
        [Fact]
        public void Registry_EveryCapabilityCitesAnExistingRepositoryPath()
        {
            var pathPattern = new Regex(@"(?:src|plans|docs)/[A-Za-z0-9_\-./]+", RegexOptions.CultureInvariant);

            foreach (var capability in EngineCapabilityRegistry.All)
            {
                var matches = pathPattern.Matches(capability.Evidence);
                Assert.True(matches.Count > 0, $"Capability '{capability.Id}' cites no repository path in its evidence.");

                foreach (Match match in matches)
                {
                    string relative = match.Value.TrimEnd('.', ',', ';', ')');
                    string onDisk = relative.Replace('/', Path.DirectorySeparatorChar);
                    Assert.True(
                        File.Exists(RepoSource.PathOf(onDisk)),
                        $"Capability '{capability.Id}' cites '{relative}', which does not exist.");
                }
            }
        }

        [Fact]
        public void Registry_RoutedOperationsComeOnlyFromParityCapabilities()
        {
            var routed = EngineCapabilityRegistry.RoutedOperations();
            var nonParityConsumers = EngineCapabilityRegistry.All
                .Where(c => c.Status != EngineCapabilityStatus.Parity)
                .SelectMany(c => c.Consumers)
                .ToArray();
            var parityConsumers = EngineCapabilityRegistry.All
                .Where(c => c.Status == EngineCapabilityStatus.Parity)
                .SelectMany(c => c.Consumers)
                .ToArray();

            Assert.Equal(
                parityConsumers.OrderBy(o => o, StringComparer.Ordinal).ToArray(),
                routed.OrderBy(o => o, StringComparer.Ordinal).ToArray());
            Assert.Empty(routed.Intersect(nonParityConsumers, StringComparer.OrdinalIgnoreCase));
            Assert.All(routed, operation => Assert.False(string.IsNullOrWhiteSpace(operation)));
        }

        // Statuses pinned by id, not by count. Moving a capability between these sets is a
        // deliberate claim change that must come with recorded evidence and a doc update.

        /// <summary>
        /// Recorded native-oracle fixtures (no consumer): the engine's value is compared
        /// row by row with the native runtime.
        /// </summary>
        private static readonly string[] NativeOracleParityIds =
        {
            "str_format", "numeric_render", "date_display", "text_functions", "text_extended_functions",
            "numeric_math_functions", "collection_operations", "calendar_operations",
            "conditional_and_state_operations", "sdt_and_collection_json", "sdt_and_collection_xml",
        };

        /// <summary>Routed operations: each is Parity only for its documented admitted space.</summary>
        private static readonly string[] RoutedParityIds =
        {
            "parallel_rebuild_all", "udm_type_query",
        };

        private static readonly string[] UnsupportedIds = { "general_gx_surface" };

        [Fact]
        public void Registry_ParityIsLimitedToNativeOracleFixturesAndTheRoutedAdmittedSpaces()
        {
            var parity = EngineCapabilityRegistry.All
                .Where(c => c.Status == EngineCapabilityStatus.Parity)
                .Select(c => c.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                NativeOracleParityIds.Concat(RoutedParityIds).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                parity);

            Assert.Equal(
                UnsupportedIds,
                EngineCapabilityRegistry.All
                    .Where(c => c.Status == EngineCapabilityStatus.Unsupported)
                    .Select(c => c.Id)
                    .ToArray());
        }

        /// <summary>
        /// Synthetic library coverage, native storage and the emitter are not SDK/product
        /// parity: each is named so restoring a blanket Parity label turns this red.
        /// </summary>
        [Theory]
        [InlineData("udm_storage_access")]
        [InlineData("native_kb_read")]
        [InlineData("sql_navigation")]
        [InlineData("formula_navigation")]
        [InlineData("dml_navigation")]
        [InlineData("csharp_emitter")]
        [InlineData("numeric_core")]
        [InlineData("text_core")]
        [InlineData("calendar_values")]
        [InlineData("records_collections")]
        [InlineData("procedure_pipeline")]
        [InlineData("xml_projection")]
        [InlineData("kb_catalog")]
        [InlineData("subroutine_control_flow")]
        [InlineData("gam_security_authorization")]
        [InlineData("gxtest_runner")]
        [InlineData("multiagent_locking_leases")]
        [InlineData("object_versioning_history")]
        [InlineData("database_reorganization")]
        [InlineData("text_tree_roundtrip")]
        public void Registry_SyntheticOrBoundedCoverage_IsNotLabelledParity(string capabilityId)
        {
            var capability = EngineCapabilityRegistry.Find(capabilityId);

            Assert.NotNull(capability);
            Assert.Equal(EngineCapabilityStatus.Partial, capability!.Status);
            Assert.False(capability.MayRouteToEngine);
            Assert.Empty(capability.Consumers);
        }

        /// <summary>
        /// A consumer-less Parity claim has no routed contract to lean on, so it must rest on
        /// a recorded native fixture whose provenance is traceable in the repository: cited in
        /// the evidence, loaded by a gx-engine Rust test, and recorded either in the measurement
        /// ledger (plans/129) or in the consuming test's own capture comment. A filename containing
        /// "native" proves none of that by itself. This traces recorded provenance; it does not
        /// re-measure the rows against a live SDK, so it is not newly revalidated parity. Evidence
        /// may cite the ledger only for a fixture the ledger actually names.
        /// </summary>
        [Fact]
        public void Registry_ConsumerlessParityCitesARecordedNativeFixtureWithTraceableProvenance()
        {
            const string ledgerPath = "plans/129-implementation-progress.md";
            var nativeFixture = new Regex(
                @"src/gx-engine/(?:tests/)?fixtures/[A-Za-z0-9_\-.]*native[A-Za-z0-9_\-.]*\.tsv",
                RegexOptions.CultureInvariant);
            var captureComment = new Regex(@"(?i)(captured|recorded|measured) from[^.]{0,60}(GeneXus|native)", RegexOptions.CultureInvariant);

            string ledger = RepoSource.Read("plans", "129-implementation-progress.md");
            string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(RepoSource.PathOf("plans", "129-implementation-progress.md")))!;
            var rustFiles = Directory.EnumerateFiles(Path.Combine(repoRoot, "src", "gx-engine"), "*.rs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "target" + Path.DirectorySeparatorChar))
                .ToDictionary(file => file, file => File.ReadAllText(file));

            foreach (var capability in EngineCapabilityRegistry.All.Where(c =>
                c.Status == EngineCapabilityStatus.Parity && c.Consumers.Length == 0))
            {
                var fixtures = nativeFixture.Matches(capability.Evidence).Select(m => m.Value).Distinct().ToArray();
                Assert.True(fixtures.Length > 0,
                    $"Parity capability '{capability.Id}' has no consumer and cites no recorded native fixture.");

                foreach (string fixture in fixtures)
                {
                    string fileName = Path.GetFileName(fixture);
                    var consumers = rustFiles.Where(f => f.Value.Contains(fileName)).ToArray();
                    Assert.True(consumers.Length > 0,
                        $"Fixture '{fileName}' cited by '{capability.Id}' is not loaded by any gx-engine Rust source.");

                    bool ledgerNamesFixture = ledger.Contains(fileName);
                    Assert.True(
                        ledgerNamesFixture || consumers.Any(f => captureComment.IsMatch(f.Value)),
                        $"Fixture '{fileName}' cited by '{capability.Id}' is neither named in {ledgerPath} nor described as captured from the native runtime by its Rust test.");
                    if (!ledgerNamesFixture)
                        Assert.DoesNotContain(ledgerPath, capability.Evidence);
                }
            }
        }

        [Fact]
        public void Registry_NonParityCapabilitiesDeclareNoConsumers()
        {
            Assert.All(
                EngineCapabilityRegistry.All.Where(c => c.Status != EngineCapabilityStatus.Parity),
                c => Assert.Empty(c.Consumers));
        }

        /// <summary>
        /// Pins the operations the engine actually serves and their capability records.
        /// Read/edit are not in this set: they are deferred by policy (next tests).
        /// </summary>
        [Fact]
        public void Registry_ServedOperationsAndTheirCapabilityRecordsArePinned()
        {
            Assert.Equal(
                new[] { "genexus_lifecycle.build", "genexus_lifecycle.rebuild", "genexus_query" },
                EngineCapabilityRegistry.RoutedOperations().OrderBy(o => o, StringComparer.Ordinal).ToArray());

            Assert.Equal(
                new[] { "genexus_lifecycle.build", "genexus_lifecycle.rebuild" },
                EngineCapabilityRegistry.Find("parallel_rebuild_all")!.Consumers);
            Assert.Equal(new[] { "genexus_query" }, EngineCapabilityRegistry.Find("udm_type_query")!.Consumers);
            Assert.Empty(EngineCapabilityRegistry.Find("udm_storage_access")!.Consumers);

            // Every consumer is declared by exactly one capability: routing must be unambiguous.
            var consumers = EngineCapabilityRegistry.All.SelectMany(c => c.Consumers).ToArray();
            Assert.Equal(consumers.Length, consumers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        /// <summary>
        /// The deferral policy is the only place read/edit are named. It reports the original
        /// storage record as their capability id, and the registry never claims them.
        /// </summary>
        [Fact]
        public void DeferralPolicy_PinsReadAndEditToTheStorageRecordWithoutAnyParityClaim()
        {
            Assert.Equal(
                new[] { "genexus_edit", "genexus_read" },
                EngineRoutedOperations.DeferredOperationCapabilityIds.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.All(
                EngineRoutedOperations.DeferredOperationCapabilityIds.Values,
                id => Assert.Equal("udm_storage_access", id));

            var storage = EngineCapabilityRegistry.Find("udm_storage_access")!;
            Assert.Equal(EngineCapabilityStatus.Partial, storage.Status);
            Assert.Contains("versionToken", storage.Notes);
            Assert.Contains("DeferredOperationCapabilityIds", storage.Notes);
            Assert.Null(EngineCapabilityRegistry.Find("udm_read_edit_deferral"));

            foreach (string deferred in EngineRoutedOperations.DeferredOperationCapabilityIds.Keys)
            {
                // No capability, of any status, declares a deferred operation as a consumer, and the
                // router-visible served set never contains it.
                Assert.DoesNotContain(EngineCapabilityRegistry.All, c =>
                    c.Consumers.Contains(deferred, StringComparer.OrdinalIgnoreCase));
                Assert.DoesNotContain(deferred, EngineCapabilityRegistry.RoutedOperations(), StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// A Parity record must serve something: one whose consumers are all deferred (or that
        /// has none and no native fixture) would count a deferral as parity.
        /// </summary>
        [Fact]
        public void Registry_NoParityRecordIsMadeOfDeferredOperations()
        {
            foreach (var capability in EngineCapabilityRegistry.All.Where(c => c.Status == EngineCapabilityStatus.Parity))
            {
                Assert.DoesNotContain(capability.Consumers, consumer =>
                    EngineRoutedOperations.DeferredOperationCapabilityIds.ContainsKey(consumer));
            }
        }

        [Fact]
        public void Registry_QueryParityRecordCitesTheBoundedHistoricalDifferentialHonestly()
        {
            var query = EngineCapabilityRegistry.Find("udm_type_query")!;

            Assert.Equal(EngineCapabilityStatus.Parity, query.Status);
            // The evidence names the recorded differential, its bounds and that it was not re-run;
            // repository tests are described as contract tests, not as that differential.
            Assert.Contains("Recorded historical", query.Evidence);
            Assert.Contains("not re-run", query.Evidence);
            Assert.Contains("nativeServed=45", query.Evidence);
            Assert.Contains("0 differences", query.Evidence);
            Assert.Contains("gatewaySha256=b1bf724d3f5de9149cd8f768ed786079151390d9667d502c0cb84a9e2b875b86", query.Evidence);
            Assert.Contains("workerSha256=3f725129c7aeb5561c2550c204b579a95d0874590b5941647ce71d114d63e439", query.Evidence);
            Assert.Contains("not a claim for other KBs", query.Notes);
            Assert.Contains("stub engine", query.Notes);
        }

        /// <summary>
        /// date_display is not named by the plans/129 ledger. Its provenance is the Rust capture
        /// comment and the measured-rules record in VALIDATION.md; the evidence must cite that
        /// record, and the documentation must describe the guard as ledger OR capture comment.
        /// </summary>
        [Fact]
        public void Registry_DateDisplayCitesValidationRecordAndDocsDescribeProvenanceAlternatives()
        {
            var dateDisplay = EngineCapabilityRegistry.Find("date_display")!;
            Assert.Contains("src/gx-engine/VALIDATION.md", dateDisplay.Evidence);
            Assert.DoesNotContain("plans/129-implementation-progress.md", dateDisplay.Evidence);

            string validation = RepoSource.Read("src", "gx-engine", "VALIDATION.md");
            Assert.Contains("date_display_native.tsv", validation);
            Assert.Contains("registered as `date_display`", validation);

            string docs = RepoSource.Read("docs", "engine_backend.md");
            Assert.Contains("either in that ledger or in a captured-from-native comment", docs);
        }

        // --------------------------------------------------------------- locator

        [Fact]
        public void Locator_ExplicitExecutableIsAuthoritative_WhenItExists()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", "@echo off\r\necho gx-engine 9.9.9\r\n");
                var settings = EngineBackendSettings.Resolve(new Configuration
                {
                    Engine = new EngineConfig { Backend = "gxengine", Executable = stub }
                });

                Assert.Equal(stub, GxEngineLocator.Locate(settings));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void Locator_ExplicitExecutableMissing_ReturnsNullInsteadOfADifferentBinary()
        {
            string tempDir = NewTempDir();
            try
            {
                string missing = Path.Combine(tempDir, "does-not-exist", "gx-engine.exe");
                var settings = EngineBackendSettings.Resolve(new Configuration
                {
                    Engine = new EngineConfig { Backend = "gxengine", Executable = missing }
                });

                Assert.Null(GxEngineLocator.Locate(settings));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ----------------------------------------------------------- probe client

        [Theory]
        [InlineData("gx-engine 0.1.0", "0.1.0")]
        [InlineData("gx-engine 9.9.9\r\n", "9.9.9")]
        [InlineData("\r\ngx-engine 1.2.3\n", "1.2.3")]
        public void VersionParsing_AcceptsTheHandshakeLine(string stdout, string expected)
        {
            Assert.True(GxEngineClient.TryParseVersionOutput(stdout, out string version));
            Assert.Equal(expected, version);
        }

        [Theory]
        [InlineData("")]
        [InlineData("hello")]
        [InlineData("warning: something\r\ngx-engine 0.1.0")]
        [InlineData("gx-engine ")]
        [InlineData("gx-engine 0.1.0 extra")]
        public void VersionParsing_RejectsAnythingElse(string stdout)
        {
            Assert.False(GxEngineClient.TryParseVersionOutput(stdout, out _));
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_WithStubEngine_ReportsTheVersion()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", "@echo off\r\necho gx-engine 9.9.9\r\n");
                var settings = EngineBackendSettings.Resolve(new Configuration
                {
                    Engine = new EngineConfig { Backend = "gxengine", Executable = stub }
                });

                var probe = await GxEngineClient.ProbeAsync(settings);

                Assert.True(probe.Available);
                Assert.Equal("9.9.9", probe.Version);
                Assert.Equal(stub, probe.Executable);
                Assert.Null(probe.ErrorCode);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_WithMissingExecutable_ReportsEngineNotFound()
        {
            string tempDir = NewTempDir();
            try
            {
                var settings = EngineBackendSettings.Resolve(new Configuration
                {
                    Engine = new EngineConfig { Backend = "gxengine", Executable = Path.Combine(tempDir, "nope.exe") }
                });

                var probe = await GxEngineClient.ProbeAsync(settings);

                Assert.False(probe.Available);
                Assert.Equal("EngineNotFound", probe.ErrorCode);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_WithFailingExecutable_ReportsProbeFailure()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", "@echo off\r\necho boom 1>&2\r\nexit /b 3\r\n");
                var settings = EngineBackendSettings.Resolve(new Configuration
                {
                    Engine = new EngineConfig { Backend = "gxengine", Executable = stub }
                });

                var probe = await GxEngineClient.ProbeAsync(settings);

                Assert.False(probe.Available);
                Assert.Equal("EngineProbeFailed", probe.ErrorCode);
                Assert.Contains("3", probe.ErrorMessage);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_WithUnrecognizedOutput_ReportsVersionUnrecognized()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", "@echo off\r\necho totally-not-an-engine\r\n");
                var settings = EngineBackendSettings.Resolve(new Configuration
                {
                    Engine = new EngineConfig { Backend = "gxengine", Executable = stub }
                });

                var probe = await GxEngineClient.ProbeAsync(settings);

                Assert.False(probe.Available);
                Assert.Equal("EngineVersionUnrecognized", probe.ErrorCode);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_WithTheRealCheckoutEngine_ReportsItsVersion()
        {
            // End-to-end against the real binary when this checkout has a cargo
            // build (skipped otherwise; CI without cargo does not fail here).
            string? repoRoot = TryFindRepoRoot();
            if (repoRoot == null) return;
            string? enginePath = new[]
            {
                Path.Combine(repoRoot, "src", "gx-engine", "target", "release", "gx-engine.exe"),
                Path.Combine(repoRoot, "src", "gx-engine", "target", "debug", "gx-engine.exe"),
            }.FirstOrDefault(File.Exists);
            if (enginePath == null) return;

            string cargoToml = File.ReadAllText(Path.Combine(repoRoot, "src", "gx-engine", "Cargo.toml"));
            var versionMatch = Regex.Match(cargoToml, "^version\\s*=\\s*\"([^\"]+)\"", RegexOptions.Multiline);
            Assert.True(versionMatch.Success, "Cargo.toml must declare the package version");

            var settings = EngineBackendSettings.Resolve(new Configuration
            {
                Engine = new EngineConfig { Backend = "gxengine", Executable = enginePath }
            });

            var probe = await GxEngineClient.ProbeAsync(settings);

            Assert.True(probe.Available, (probe.ErrorMessage ?? string.Empty)
                + " (stale cargo output? rebuild: cargo build --release --manifest-path src/gx-engine/Cargo.toml)");
            Assert.Equal(versionMatch.Groups[1].Value, probe.Version);
        }

        // ------------------------------------------------------------ doctor view

        [Fact]
        public void DoctorEngineSection_ExplicitSdkBackend_IsStaticAndSdkOnly()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, null);
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, null);

            var section = Program.BuildEngineDoctorSection(new Configuration
            {
                Engine = new EngineConfig { Backend = "sdk" }
            });

            Assert.Equal("sdk", section["backend"]?.ToString());
            Assert.Equal("sdk", section["fallback"]?.ToString());
            Assert.Null(section["available"]);
            Assert.Equal(
                EngineCapabilityRegistry.All.Count,
                section["capabilities"]!["parity"]!.Value<int>()
                    + section["capabilities"]!["partial"]!.Value<int>()
                    + section["capabilities"]!["unsupported"]!.Value<int>());
            Assert.IsType<JArray>(section["capabilities"]!["routedOperations"]);
        }

        /// <summary>
        /// Doctor must not present synthetic library coverage as parity: its counts come
        /// from the pinned status sets, and every other registered capability is Partial.
        /// </summary>
        [Fact]
        public void DoctorEngineSection_CapabilityCountsReflectThePinnedStatusSets()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, null);
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, null);
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, null);

            var counts = Program.BuildEngineDoctorSection(new Configuration
            {
                Engine = new EngineConfig { Backend = "sdk" }
            })["capabilities"]!;

            int parity = NativeOracleParityIds.Length + RoutedParityIds.Length;
            Assert.Empty(counts["routedOperations"]!.Values<string>());
            Assert.Empty(counts["deferredOperations"]!.Values<string>());
            Assert.Equal(parity, counts["parity"]!.Value<int>());
            Assert.Equal(UnsupportedIds.Length, counts["unsupported"]!.Value<int>());
            Assert.Equal(
                EngineCapabilityRegistry.All.Count - parity - UnsupportedIds.Length,
                counts["partial"]!.Value<int>());
        }

        [Theory]
        [InlineData(null, "gxengine")]
        [InlineData("auto", "auto")]
        [InlineData("gxengine", "gxengine")]
        public void DoctorEngineSection_EnabledButMissing_ReportsUnavailableAndWarns(string? backend, string expected)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, Path.Combine(tempDir, "nope.exe"));

                var doctor = Program.BuildGatewayDoctorEnvelope(null);

                Assert.Equal("ok", doctor["status"]?.ToString());
                var engine = doctor["result"]!["engine"]!;
                Assert.Equal(expected, engine["backend"]?.ToString());
                Assert.False(engine["available"]!.Value<bool>());
                Assert.Equal("EngineNotFound", engine["error"]!["code"]?.ToString());

                var warnings = (JArray)doctor["result"]!["warnings"]!;
                Assert.Contains(warnings, warning => warning.ToString().Contains("gx-engine"));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData(null, "gxengine")]
        [InlineData("auto", "auto")]
        [InlineData("gxengine", "gxengine")]
        public void DoctorEngineSection_EnabledWithStubEngine_ReportsTheProbedVersion(string? backend, string expected)
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", "@echo off\r\necho gx-engine 9.9.9\r\n");
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var doctor = Program.BuildGatewayDoctorEnvelope(null);

                Assert.Equal("ok", doctor["status"]?.ToString());
                var engine = doctor["result"]!["engine"]!;
                Assert.Equal(expected, engine["backend"]?.ToString());
                var routed = engine["capabilities"]!["routedOperations"]!.Values<string>().ToArray();
                var deferred = engine["capabilities"]!["deferredOperations"]!.Values<string>().ToArray();
                if (expected == "auto")
                {
                    Assert.Equal(new[] { "genexus_lifecycle.build", "genexus_lifecycle.rebuild" }, routed);
                    Assert.Empty(deferred);
                }
                else
                {
                    // Only genuinely served operations are routed; read/edit are deferred, never routed.
                    Assert.Equal(
                        new[] { "genexus_lifecycle.build", "genexus_lifecycle.rebuild", "genexus_query" },
                        routed.OrderBy(o => o, StringComparer.Ordinal).ToArray());
                    Assert.DoesNotContain("genexus_read", routed);
                    Assert.DoesNotContain("genexus_edit", routed);
                    Assert.Equal(new[] { "genexus_edit", "genexus_read" }, deferred);
                }
                Assert.True(engine["available"]!.Value<bool>());
                Assert.Equal("9.9.9", engine["version"]?.ToString());
                Assert.Equal(stub, engine["executable"]?.ToString());
                Assert.True(engine["error"] == null || engine["error"]!.Type == JTokenType.Null);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ----------------------------------------------------- strict config rules

        [Theory]
        [InlineData("auto")]
        [InlineData("sdk")]
        [InlineData("gxengine")]
        public void StrictConfig_AcceptsAValidEngineSection(string backend)
        {
            string tempDir = NewTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                File.WriteAllText(configPath, StrictConfigWithEngine(
                    new JProperty("Engine", new JObject
                    {
                        ["Backend"] = backend, ["Fallback"] = "error", ["TimeoutSeconds"] = 30
                    }).ToString()));

                var config = ParseConfig(configPath);

                Assert.NotNull(config.Engine);
                Assert.Equal(backend, config.Engine!.Backend);
                Assert.Equal("error", config.Engine.Fallback);
                Assert.Equal(30, config.Engine.TimeoutSeconds);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData(@"""Engine"": { ""Backend"": ""claude"" }", "Engine.Backend")]
        [InlineData(@"""Engine"": { ""Fallback"": ""yolo"" }", "Engine.Fallback")]
        [InlineData(@"""Engine"": { ""TimeoutSeconds"": 0 }", "Engine.TimeoutSeconds")]
        [InlineData(@"""Engine"": { ""SomethingElse"": true }", "Unknown member 'Engine.SomethingElse'")]
        public void StrictConfig_RejectsInvalidEngineSections(string section, string expectedMessage)
        {
            string tempDir = NewTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                File.WriteAllText(configPath, StrictConfigWithEngine(section));

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));

                Assert.Contains(expectedMessage, error.Message);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void StrictConfig_RejectsEngineEnvironmentOverrides()
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, "gxengine");
                string configPath = Path.Combine(tempDir, "config.json");
                File.WriteAllText(configPath, StrictConfigWithEngine(@"""Engine"": { ""Backend"": ""gxengine"" }"));

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));

                Assert.Contains(EngineBackendSettings.BackendEnvVar, error.Message);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void LegacyConfig_ReadsTheEngineSectionWithoutValidation()
        {
            string tempDir = NewTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                File.WriteAllText(configPath, @"{ ""Environment"": { ""ResolutionPolicy"": ""legacy"" }, ""Engine"": { ""Backend"": ""gxengine"" } }");

                var config = ParseConfig(configPath);

                Assert.Equal("gxengine", config.Engine?.Backend);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------------------------ utils

        private static string StrictConfigWithEngine(string engineSection) => @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" },
  " + engineSection + @"
}";

        private static Configuration ParseConfig(string path)
        {
            var method = typeof(Configuration).GetMethod("ParseConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            try
            {
                return (Configuration)method!.Invoke(null, new object[] { path })!;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static string NewTempDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-engine-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        /// <summary>
        /// Walks up from the test output directory looking for the checkout root
        /// (src/gx-engine/Cargo.toml). Returns null when the tests run outside a
        /// checkout with the engine sources.
        /// </summary>
        private static string? TryFindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "gx-engine", "Cargo.toml")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static string CreateStub(string directory, string name, string content)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, content, System.Text.Encoding.ASCII);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
