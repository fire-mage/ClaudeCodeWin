using System.IO;

namespace ClaudeCodeWin.Services.Highlighting;

public static class LanguageDetector
{
    private static readonly CSharpTokenizer CSharpInstance = new();

    public static ILanguageTokenizer? GetTokenizer(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => CSharpInstance,
            _ => null
        };
    }
}
