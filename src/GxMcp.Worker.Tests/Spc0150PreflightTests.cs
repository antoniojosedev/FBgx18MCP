using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class Spc0150PreflightTests
    {
        [Fact]
        public void ForEach_WithAttributeWrite_Flagged()
        {
            string src = @"For each Aluno
    AluCod = 0
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Single(hits);
            Assert.Equal(2, hits[0].Line);
        }

        [Fact]
        public void ForEach_WithVariableWrite_NotFlagged()
        {
            string src = @"For each Aluno
    &Counter = &Counter + 1
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Empty(hits);
        }

        [Fact]
        public void OutsideForEach_AttributeWrite_NotFlagged()
        {
            string src = @"AluCod = 0
For each Aluno
    &x = 1
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Empty(hits);
        }

        [Fact]
        public void NestedForEach_AttributeWrite_Flagged()
        {
            string src = @"For each Aluno
    For each Curso
        CurCod = 99
    endfor
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Single(hits);
            Assert.Equal(3, hits[0].Line);
        }

        [Fact]
        public void Comparison_NotFlagged()
        {
            // == is comparison, not assignment — must not trigger.
            string src = @"For each Aluno
    if AluCod == 0
        &x = 1
    endif
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Empty(hits);
        }
    }
}
