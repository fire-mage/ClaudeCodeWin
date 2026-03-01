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
    private static readonly JavaScriptTokenizer JavaScriptInstance = new();
    private static readonly JavaScriptCompletionProvider JavaScriptCompletionInstance = new();
    private static readonly JavaTokenizer JavaInstance = new();
    private static readonly JavaCompletionProvider JavaCompletionInstance = new();
    private static readonly CppTokenizer CppInstance = new();
    private static readonly CppCompletionProvider CppCompletionInstance = new();
    private static readonly GoTokenizer GoInstance = new();
    private static readonly GoCompletionProvider GoCompletionInstance = new();
    private static readonly RustTokenizer RustInstance = new();
    private static readonly RustCompletionProvider RustCompletionInstance = new();
    private static readonly PhpTokenizer PhpInstance = new();
    private static readonly PhpCompletionProvider PhpCompletionInstance = new();
    private static readonly SwiftTokenizer SwiftInstance = new();
    private static readonly SwiftCompletionProvider SwiftCompletionInstance = new();
    private static readonly KotlinTokenizer KotlinInstance = new();
    private static readonly KotlinCompletionProvider KotlinCompletionInstance = new();
    private static readonly SqlTokenizer SqlInstance = new();
    private static readonly SqlCompletionProvider SqlCompletionInstance = new();

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
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".mts" or ".cjs" or ".cts" => JavaScriptInstance,
            ".java" => JavaInstance,
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" or ".hxx" => CppInstance,
            ".go" => GoInstance,
            ".rs" => RustInstance,
            ".php" or ".phtml" => PhpInstance,
            ".swift" => SwiftInstance,
            ".kt" or ".kts" => KotlinInstance,
            ".sql" => SqlInstance,
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
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".mts" or ".cjs" or ".cts" => JavaScriptCompletionInstance,
            ".java" => JavaCompletionInstance,
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" or ".hxx" => CppCompletionInstance,
            ".go" => GoCompletionInstance,
            ".rs" => RustCompletionInstance,
            ".php" or ".phtml" => PhpCompletionInstance,
            ".swift" => SwiftCompletionInstance,
            ".kt" or ".kts" => KotlinCompletionInstance,
            ".sql" => SqlCompletionInstance,
            _ => null
        };
    }
}
