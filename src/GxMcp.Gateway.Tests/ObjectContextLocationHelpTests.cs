using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #651: `genexus_object_context` returned `module: "Root Module"` next to a `path` that
    /// named a Folder, and callers used `module` for write-scope checks. The help must say what each
    /// location field means so the contradiction is not re-read as a defect.
    /// </summary>
    public class ObjectContextLocationHelpTests
    {
        [Fact]
        public void ObjectContextHelp_DistinguishesTheGeneXusModuleFromTheContainerPath()
        {
            string help = ToolHelpCatalog.Get("genexus_object_context") ?? string.Empty;

            Assert.NotEmpty(help);
            Assert.Contains("`parentPath`", help, StringComparison.Ordinal);
            Assert.Contains("`parent`", help, StringComparison.Ordinal);
            Assert.Contains("`module` is the owning GeneXus **Module**, not a Folder", help, StringComparison.Ordinal);
            Assert.Contains("Check write scope with `parentPath`/`path`, never with `module`", help, StringComparison.Ordinal);
        }
    }
}
