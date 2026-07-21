using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 2.1): Worker code references Artech.* assemblies that ship
    // with a GeneXus 18 install, not via NuGet. The Worker process installs an
    // AssemblyResolve hook in Program.Main pointing at GX_PROGRAM_DIR, but the
    // test runner never invokes Main — so when a unit test triggers JIT of a
    // method that touches KBObject (e.g. SourceSearchService.SearchCore), the
    // CLR cannot locate the dependency and the test fails with a
    // FileNotFoundException instead of running the assertion.
    //
    // This module initializer wires the same probing fallback into the test
    // AppDomain. It is best-effort: if GX is not installed the resolver simply
    // returns null, the dependent test surface fails as before, but tests that
    // don't transitively need KBObject (the bulk of the suite) keep passing.
    internal static class TestAssemblyResolver
    {
        private static bool _installed;

        [ModuleInitializer]
        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
            if (string.IsNullOrWhiteSpace(gxPath))
            {
                gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            }
            if (!Directory.Exists(gxPath)) return;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    string name = new AssemblyName(args.Name).Name + ".dll";
                    string path = Path.Combine(gxPath, name);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                    // Plan 024/025 (2026-07-21): some Artech.Packages.* assemblies
                    // (GAM, TeamDevClient.BL, ...) ship under GX_PROGRAM_DIR\Packages
                    // rather than the GX root, and are Private=False in the Worker
                    // .csproj (relying on the SDK's own package loader / the Worker's
                    // AssemblyResolve at runtime). Probe that subfolder too so tests
                    // that JIT methods touching those types don't fail with
                    // FileNotFoundException before the test body even runs.
                    string packagesPath = Path.Combine(gxPath, "Packages", name);
                    if (File.Exists(packagesPath)) return Assembly.LoadFrom(packagesPath);
                }
                catch { }
                return null;
            };
        }
    }
}

// ModuleInitializerAttribute lives in System.Runtime.CompilerServices for
// .NET 5+ but on net48 we need a polyfill. The attribute is only consulted
// by the compiler, so a same-namespace stub is sufficient.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
