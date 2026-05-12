using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceParserTests
    {
        [Fact]
        public void ParseCalls_QualifiedMemberCall_ExtractsCalleeAndArgs()
        {
            string src = "&p = DPParametros.Udp(373, &cliente, \"abc\")";
            var calls = SourceParser.ParseCalls(src);
            var call = calls.Single();
            Assert.Equal("DPParametros.Udp", call.Callee);
            Assert.Equal(3, call.Args.Count);
            Assert.Equal("373", call.Args[0]);
            Assert.Equal("&cliente", call.Args[1]);
            Assert.Equal("\"abc\"", call.Args[2]);
            Assert.Equal(1, call.LineNumber);
        }

        [Fact]
        public void ParseCalls_IgnoresLineComments()
        {
            string src = "// DPParametros.Udp(999, x)\n&y = Foo(1)";
            var calls = SourceParser.ParseCalls(src);
            Assert.Single(calls);
            Assert.Equal("Foo", calls[0].Callee);
            Assert.Equal(2, calls[0].LineNumber);
        }

        [Fact]
        public void ParseCalls_IgnoresBlockComments()
        {
            string src = "/* DPParametros.Udp(1) */\nBar(2)";
            var calls = SourceParser.ParseCalls(src);
            Assert.Single(calls);
            Assert.Equal("Bar", calls[0].Callee);
        }

        [Fact]
        public void ParseCalls_NestedParens_ParseArgsCorrectly()
        {
            string src = "Foo(Bar(1,2), 3)";
            var calls = SourceParser.ParseCalls(src);
            var foo = calls.First(c => c.Callee == "Foo");
            Assert.Equal(2, foo.Args.Count);
            Assert.Equal("Bar(1,2)", foo.Args[0]);
            Assert.Equal("3", foo.Args[1]);
        }

        [Fact]
        public void ParseCalls_StringWithCommaAndParen_DoesNotSplitArg()
        {
            string src = "Foo(\"a,b)\", 2)";
            var calls = SourceParser.ParseCalls(src);
            var foo = calls.Single();
            Assert.Equal(2, foo.Args.Count);
            Assert.Equal("\"a,b)\"", foo.Args[0]);
            Assert.Equal("2", foo.Args[1]);
        }

        [Fact]
        public void ParseCalls_MultipleLines_LineNumberCorrect()
        {
            string src = "line1\nline2()\nline3()";
            var calls = SourceParser.ParseCalls(src);
            Assert.Equal(2, calls.Count);
            Assert.Equal(2, calls[0].LineNumber);
            Assert.Equal(3, calls[1].LineNumber);
        }

        [Fact]
        public void ParseCalls_EmptyArgs_ReturnsEmptyList()
        {
            string src = "Foo()";
            var calls = SourceParser.ParseCalls(src);
            Assert.Empty(calls.Single().Args);
        }

        [Fact]
        public void ParseCalls_IncludeCommentsTrue_DoesNotSkipCommentCalls()
        {
            string src = "// Foo(1)";
            var calls = SourceParser.ParseCalls(src, includeComments: true);
            Assert.Single(calls);
            Assert.Equal("Foo", calls[0].Callee);
        }
    }
}
