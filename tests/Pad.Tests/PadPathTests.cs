using Pad.Api.Storage;

namespace Pad.Tests;

public class PadPathTests
{
    [Theory]
    [InlineData("a.md", "a.md")]
    [InlineData("/a/b/c.txt", "a/b/c.txt")]
    [InlineData("a/b/", "a/b")]
    [InlineData("my notes/ünï code.md", "my notes/ünï code.md")]
    public void Valid_paths_normalize(string input, string expected) => Assert.Equal(expected, PadPath.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a/\u0000b")]
    [InlineData("a/\nb")]
    [InlineData("a/.padkeep")]
    public void Invalid_paths_throw(string input) => Assert.Throws<BadPathException>(() => PadPath.Normalize(input));

    [Fact] public void Empty_allowed_for_root() => Assert.Equal("", PadPath.Normalize(null, allowEmpty: true));
    [Fact] public void Too_long_throws() => Assert.Throws<BadPathException>(() => PadPath.Normalize(new string('a', 1025)));
}
