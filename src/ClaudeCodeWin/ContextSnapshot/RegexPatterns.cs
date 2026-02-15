using System.Text.RegularExpressions;

namespace ClaudeCodeWin.ContextSnapshot;

public static partial class RegexPatterns
{
    // === C# Patterns ===

    [GeneratedRegex(@"public\s+(static\s+)?(abstract\s+)?(partial\s+)?(class|interface|record|enum)\s+(\w+)(?:\s*<[^>]+>)?(?:\s*:\s*(.+))?", RegexOptions.Compiled)]
    public static partial Regex CSharpClassDeclaration();

    [GeneratedRegex(@"\[Http(Get|Post|Put|Delete|Patch)(?:\(""([^""]*)""\))?\]", RegexOptions.Compiled)]
    public static partial Regex HttpMethodAttribute();

    [GeneratedRegex(@"\[Route\(""([^""]*)""\)\]", RegexOptions.Compiled)]
    public static partial Regex RouteAttribute();

    [GeneratedRegex(@"\[Authorize(?:\(([^)]*)\))?\]", RegexOptions.Compiled)]
    public static partial Regex AuthorizeAttribute();

    [GeneratedRegex(@"\[AllowAnonymous\]", RegexOptions.Compiled)]
    public static partial Regex AllowAnonymousAttribute();

    [GeneratedRegex(@"private\s+readonly\s+([\w<>\[\]?,\s]+?)\s+_(\w+);", RegexOptions.Compiled)]
    public static partial Regex ConstructorDependency();

    [GeneratedRegex(@"public\s+(virtual\s+)?(required\s+)?([\w<>\[\]?]+)\s+(\w+)\s*\{\s*get;\s*set;\s*\}", RegexOptions.Compiled)]
    public static partial Regex PropertyDeclaration();

    [GeneratedRegex(@"public\s+(static\s+)?(async\s+)?(override\s+)?([\w<>\[\]?,\s]+?)\s+(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled)]
    public static partial Regex MethodDeclaration();

    [GeneratedRegex(@"\[ApiController\]", RegexOptions.Compiled)]
    public static partial Regex ApiControllerAttribute();

    [GeneratedRegex(@"public\s+virtual\s+ICollection<(\w+)>", RegexOptions.Compiled)]
    public static partial Regex NavigationCollection();

    // === TypeScript Patterns ===

    [GeneratedRegex(@"<Route\s+path=""([^""]+)""\s+element=\{(.+?)\}\s*/?>", RegexOptions.Compiled)]
    public static partial Regex ReactRoute();

    [GeneratedRegex(@"<(\w+)\s*/?>", RegexOptions.Compiled)]
    public static partial Regex JsxComponent();

    [GeneratedRegex(@"api\.(get|post|put|delete|patch)\s*(?:<[^>]*>)?\s*\(\s*['""`]([^'""` ]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex ApiCall();

    [GeneratedRegex(@"export\s+(const|function|interface|type|class|default\s+function|enum)\s+(\w+)", RegexOptions.Compiled)]
    public static partial Regex TypeScriptExport();

    [GeneratedRegex(@"import\s+(\w+)\s+from\s+['""]\.\/pages\/(\w+)['""]", RegexOptions.Compiled)]
    public static partial Regex PageImport();
}
