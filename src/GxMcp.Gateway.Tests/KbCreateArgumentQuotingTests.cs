using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// <c>genexus_kb action=create</c> failed on a default GeneXus 18 install with
    /// <c>MSB1008: Only one project can be specified. Switch: Files</c>: the template lives under
    /// <c>C:\Program Files (x86)\GeneXus\GeneXus18</c>, its <c>/p:</c> value went on the command
    /// line unquoted, and the child split it at the space into a second positional argument.
    ///
    /// <para>The split is checked with <c>CommandLineToArgvW</c>, the Windows parser that applies
    /// the same rules a .NET Framework child uses to break its command line into tokens.</para>
    /// </summary>
    public class KbCreateArgumentQuotingTests
    {
        private const string Project = @"C:\Users\Some User\AppData\Local\Temp\gxmcp_create_kb_x.proj";
        private const string KbDir = @"C:\KBs\My KB teste";
        private const string Template = @"C:\Program Files (x86)\GeneXus\GeneXus18\Templates\csharp\netcore.kbtemplate";
        private const string Server = @"(LocalDB)\MSSQLLocalDB";
        private const string DbName = "gx kb teste";

        [Fact]
        public void PathsWithSpacesStayOneArgumentEach()
        {
            string arguments = KbCreateHelper.BuildMsBuildArguments(
                Project, KbDir, Template, Server, DbName, integrated: true, dbUser: null, passwordResponseFile: null);

            string[] argv = Split(arguments);

            // MSBuild takes every token that is not a switch as a project; more than one is MSB1008.
            var positional = argv.Where(a => !a.StartsWith("/", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { Project }, positional);

            var properties = Properties(argv);
            Assert.Equal(KbDir, properties["KBDirectory"]);
            Assert.Equal(Template, properties["KBTemplate"]);
            Assert.Equal(Server, properties["KBDbServer"]);
            Assert.Equal(DbName, properties["KBDbName"]);
            Assert.Equal("True", properties["KBIntegratedSecurity"]);
        }

        [Fact]
        public void SqlLoginUserWithSpacesStaysOneArgument()
        {
            const string rsp = @"C:\Users\Some User\AppData\Local\Temp\gxmcp_create_kb_x.rsp";
            string arguments = KbCreateHelper.BuildMsBuildArguments(
                Project, KbDir, Template, Server, DbName, integrated: false, dbUser: "kb user", passwordResponseFile: rsp);

            string[] argv = Split(arguments);

            Assert.Equal(new[] { Project }, argv.Where(a => !a.StartsWith("/", StringComparison.Ordinal) && !a.StartsWith("@", StringComparison.Ordinal)));
            Assert.Equal("kb user", Properties(argv)["KBDbUser"]);
            Assert.Contains("@" + rsp, argv);
        }

        [Fact]
        public void TrailingBackslashDoesNotSwallowTheNextArgument()
        {
            // A quoted value ending in a backslash would otherwise escape its own closing quote.
            string arguments = KbCreateHelper.BuildMsBuildArguments(
                Project, @"C:\KBs\My KB teste\", Template, Server, DbName, integrated: true, dbUser: null, passwordResponseFile: null);

            var properties = Properties(Split(arguments));
            Assert.Equal(@"C:\KBs\My KB teste\", properties["KBDirectory"]);
            Assert.Equal(Template, properties["KBTemplate"]);
        }

        private static Dictionary<string, string> Properties(IEnumerable<string> argv) =>
            argv.Where(a => a.StartsWith("/p:", StringComparison.Ordinal))
                .Select(a => a.Substring(3).Split('=', 2))
                .ToDictionary(kv => kv[0], kv => kv[1]);

        private static string[] Split(string arguments)
        {
            // Index 0 is the program name, which follows different rules; give it a fixed one.
            IntPtr ptr = CommandLineToArgvW("MSBuild.exe " + arguments, out int argc);
            Assert.NotEqual(IntPtr.Zero, ptr);
            try
            {
                var result = new string[argc - 1];
                for (int i = 1; i < argc; i++)
                {
                    result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size))!;
                }
                return result;
            }
            finally
            {
                LocalFree(ptr);
            }
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
