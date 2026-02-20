using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.Tests;

public class EscapeJsonTests
{
    [Fact]
    public void EscapesBackslashes()
    {
        Assert.Equal("C:\\\\Users\\\\test", StreamJsonParser.EscapeJson("C:\\Users\\test"));
    }

    [Fact]
    public void EscapesQuotes()
    {
        Assert.Equal("say \\\"hello\\\"", StreamJsonParser.EscapeJson("say \"hello\""));
    }

    [Fact]
    public void EscapesNewlinesAndTabs()
    {
        Assert.Equal("line1\\nline2\\ttab", StreamJsonParser.EscapeJson("line1\nline2\ttab"));
    }

    [Fact]
    public void EscapesCarriageReturn()
    {
        Assert.Equal("a\\rb", StreamJsonParser.EscapeJson("a\rb"));
    }

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", StreamJsonParser.EscapeJson(""));
    }

    [Fact]
    public void PlainText_Unchanged()
    {
        Assert.Equal("hello world", StreamJsonParser.EscapeJson("hello world"));
    }
}

public class IsNewerVersionTests
{
    [Fact]
    public void NewerVersion_ReturnsTrue()
    {
        Assert.True(UpdateService.IsNewerVersion("2.0.0", "1.0.0"));
    }

    [Fact]
    public void SameVersion_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("1.0.0", "1.0.0"));
    }

    [Fact]
    public void OlderVersion_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("1.0.0", "2.0.0"));
    }

    [Fact]
    public void InvalidRemote_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("not-a-version", "1.0.0"));
    }

    [Fact]
    public void InvalidLocal_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("1.0.0", "garbage"));
    }

    [Fact]
    public void MinorVersionBump_ReturnsTrue()
    {
        Assert.True(UpdateService.IsNewerVersion("1.2.0", "1.1.0"));
    }

    [Fact]
    public void PatchVersionBump_ReturnsTrue()
    {
        Assert.True(UpdateService.IsNewerVersion("1.0.1", "1.0.0"));
    }
}

public class StripAnsiTests
{
    [Fact]
    public void CursorForward_ReplacedWithSpaces()
    {
        // \x1B[5C should become 5 spaces
        var result = ClaudeCodeDependencyService.StripAnsi("abc\x1B[5Cdef");
        Assert.Equal("abc def", result);
    }

    [Fact]
    public void ColorCodes_Stripped()
    {
        var result = ClaudeCodeDependencyService.StripAnsi("\x1B[32mGreen text\x1B[0m");
        Assert.Equal("Green text", result);
    }

    [Fact]
    public void MixedContent_CleanedProperly()
    {
        var result = ClaudeCodeDependencyService.StripAnsi("\x1B[1m\x1B[34mHello\x1B[0m World");
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void PlainText_Unchanged()
    {
        Assert.Equal("plain text", ClaudeCodeDependencyService.StripAnsi("plain text"));
    }
}
