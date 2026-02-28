using System.IO;

namespace ClaudeCodeWin.Services.Highlighting;

public static class LanguageDetector
{
    private static readonly CSharpTokenizer CSharpInstance = new();
    private static readonly CSharpCompletionProvider CSharpCompletionInstance = new();
    private static readonly HtmlTokenizer HtmlInstance = new();
    private static readonly HtmlCompletionProvider HtmlCompletionInstance = new();
    private static readonly CssTokenizer CssInstance = new();
    private static readonly CssCompletionProvider CssCompletionInstance = new();
    private static readonly PythonTokenizer PythonInstance = new();
    private static readonly PythonCompletionProvider PythonCompletionInstance = new();

    public static ILanguageTokenizer? GetTokenizer(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => CSharpInstance,
            ".html" or ".htm" => HtmlInstance,
            ".css" => CssInstance,
            ".py" => PythonInstance,
            _ => null
        };
    }

    public static ICompletionProvider? GetCompletionProvider(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => CSharpCompletionInstance,
            ".html" or ".htm" => HtmlCompletionInstance,
            ".css" => CssCompletionInstance,
            ".py" => PythonCompletionInstance,
            _ => null
        };
    }
}
