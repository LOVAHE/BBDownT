using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class PathSegmentSanitizerTests
{
    [Theory]
    [InlineData("aid/cid", "aid_cid")]
    [InlineData("aid\\cid", "aid_cid")]
    [InlineData("  episode title...  ", "episode title")]
    [InlineData("plain-value", "plain-value")]
    public void Sanitize_PreservesExistingPathRules(string input, string expected)
    {
        Assert.Equal(expected, PathSegmentSanitizer.Sanitize(input));
    }
}
