using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class ReadTokenFingerprintTests
    {
        // Independently generated hashlib vectors shared with Rust tests/read_token.rs.
        // This exercises the Worker hash, not live SDK timestamps or parent identity.
        [Theory]
        [InlineData(null, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
        [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
        [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
        [InlineData("a\u00e7\u00e3o \u2013 \u65e5\u672c\u8a9e \ud83d\ude00", "7aed77b3295efa4c872242c38871770d5716e82f2f273f5bd7193aec297b8a04")]
        [InlineData("line1\r\nline2", "d14a91a6d1c6ee83bf0c774ebecbee6d8b393b395dae29eea839c354d6fba9c0")]
        [InlineData("line1\nline2", "683376e290829b482c2655745caffa7a1dccfa10afaa62dac2b42dd6c68d0f83")]
        [InlineData("x\u0085y", "1d8c4f61b825f326dfc0302bafa2bf2e468aa52ff5dcd56ddcde96427b91d5bd")]
        [InlineData("abc\n", "edeaaff3f1774ad2888673770c6d64097e391bc362d7d6fb34982ddf0efd18cb")]
        [InlineData("abc \r\n", "fff0148214f5513e57cdeea0c3debb262f0a8fdc481fdcd480449716efb03491")]
        [InlineData("  abc", "1e763219e229d1971b9a556c34a32485e14547524fe754ec233769f77173f7af")]
        [InlineData("\ufeffabc", "1c28dc3f1f804a1ad9c9b4b4cf5e2658d16ad4ed08e3020d04a8d2865018947c")]
        public void FingerprintPreservesExactUtf8Content(string content, string expected)
        {
            Assert.Equal(expected, WriteService.ComputeContentFingerprint(content));
        }
    }
}
