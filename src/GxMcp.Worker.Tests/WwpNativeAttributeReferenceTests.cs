using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #595: a reference attribute of a native pattern element (`attribute`, `domain`) holds the KB object itself, and the
    // property bag's text form of an object is its CLR type name, so the native `attribute` of a form element never matched the
    // `<type GUID>-<name>` every caller compares against: move_control of an attribute found no element, and an `attribute` child of
    // add_tab/add_table created a duplicate. The fix reads the object and formats the reference the way the pattern serialises it.
    //
    // A KBObject cannot be built without a knowledge base, so the fake element model of the other native tests cannot exercise this
    // branch; the shape is pinned in the source and the behaviour was proved against the real package (the proofs recorded in the
    // pull request: move_control of an attribute, add_tab with an attribute child).
    public class WwpNativeAttributeReferenceTests
    {
        private static string NativeAttributeBody()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "WwpActionService.Tabs.cs")));
            int start = source.IndexOf("private static string NativeAttribute(object element, string name)", StringComparison.Ordinal);
            Assert.True(start >= 0, "NativeAttribute moved");
            int end = source.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.True(end > start, "NativeAttribute is no longer a method this guard can delimit");
            return source.Substring(start, end - start);
        }

        [Fact]
        public void AReferenceIsReadFromTheKbObjectAndFormattedAsTheTypeGuidAndName()
        {
            string body = NativeAttributeBody();

            Assert.Contains("GetPropertyValueString", body);
            Assert.Contains("m.Name == \"GetPropertyValue\"", body);
            Assert.Contains("is KBObject referenced", body);
            Assert.Contains("return GxObjectReference(referenced);", body);
        }

        [Fact]
        public void OnlyATextThatLooksLikeAClrTypeNameIsReplacedByTheReference()
        {
            string body = NativeAttributeBody();

            // A plain value (a name, a GUID-name text, a sentence) is returned untouched: the object is looked up only for
            // dotted text without spaces or dashes, which is how the bag prints an object.
            Assert.Contains("text.IndexOf('.') > 0 && text.IndexOf(' ') < 0 && text.IndexOf('-') < 0", body);
            Assert.EndsWith("return text;", body.TrimEnd());
        }
    }
}
