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

        // Production bug this catches: commented-out attribute writes
        // ("// AluCod = 0") used to be flagged because the regex anchor
        // didn't filter "//" prefixes — produced false-positive warnings
        // that survived every preflight even when the user fixed them.
        [Fact]
        public void CommentedAssignment_NotFlagged()
        {
            string src = @"For each Aluno
    // AluCod = 0
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Empty(hits);
        }

        // Production bug this catches: empty/null source threw NullReference
        // when the scanner was called from a pre-save validator on freshly
        // created events parts.
        [Fact]
        public void EmptyOrNullSource_ReturnsEmpty()
        {
            Assert.Empty(Spc0150PreflightScanner.Scan(null));
            Assert.Empty(Spc0150PreflightScanner.Scan(""));
        }

        // Production bug this catches: an extra/orphan `endfor` (malformed
        // source) used to underflow the depth counter and start flagging
        // legitimate top-level assignments. depth must clamp at zero.
        [Fact]
        public void OrphanEndfor_DoesNotUnderflowDepth()
        {
            string src = @"endfor
AluCod = 0
For each X
    OtherAttr = 1
endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            // Only the assignment inside the legit For each is flagged.
            var hit = Assert.Single(hits);
            Assert.Contains("OtherAttr", hit.Text);
        }

        // Production bug this catches: assignment *after* `endfor` (back
        // to depth 0) was being flagged because depth tracking missed
        // restoration on the endfor line.
        [Fact]
        public void AssignmentAfterEndfor_NotFlagged()
        {
            string src = @"For each Aluno
    &x = 1
endfor
AluCod = 0";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Empty(hits);
        }

        // Production bug this catches: scanner only handled \n line splits,
        // so CRLF-encoded events files (the common Windows shape) had
        // their line numbers doubled / depth-tracking broken.
        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        [InlineData("\r")]
        public void DifferentLineEndings_ProduceSameFindings(string nl)
        {
            string src = "For each Aluno" + nl + "    AluCod = 0" + nl + "endfor";
            var hits = Spc0150PreflightScanner.Scan(src);
            var hit = Assert.Single(hits);
            Assert.Equal(2, hit.Line);
        }

        // Production bug this catches: "For Each" mid-line (continuation
        // text or comment) used to bump depth and trigger spurious findings.
        // Anchor is start-of-line (after whitespace) only.
        [Fact]
        public void ForEachMidLine_DoesNotIncreaseDepth()
        {
            string src = @"// describe a For each Aluno block
AluCod = 0";
            var hits = Spc0150PreflightScanner.Scan(src);
            Assert.Empty(hits);
        }
    }
}
