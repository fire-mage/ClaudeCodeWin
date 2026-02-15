using System.IO;
using System.Text;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class CodePatternsSection : ISnapshotSection
{
    public string Title => "Code Patterns";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "8. Code Patterns");
        md.Line("Representative skeletons showing project conventions.");

        foreach (var result in results)
        {
            if (result.ProjectType == "aspnet-core")
                GenerateBackendPatterns(md, result);
            else if (result.ProjectType == "react-app")
                GenerateFrontendPatterns(md, result);
        }
    }

    private void GenerateBackendPatterns(MarkdownBuilder md, AnalysisResult result)
    {
        var controller = result.Classes
            .Where(c => c.Name.EndsWith("Controller") && c.Attributes.Contains("Authorize"))
            .OrderBy(c => Math.Abs(c.Methods.Count(m => m.HttpMethod != null) - 4))
            .FirstOrDefault();

        if (controller != null)
        {
            md.Header(3, $"{result.ProjectName} — Controller Pattern");
            md.CodeBlock(BuildControllerSkeleton(controller), "csharp");
        }

        var service = result.Classes
            .Where(c => c.Kind == "class"
                        && !c.Name.EndsWith("Controller")
                        && c.Interfaces.Any(i => i.StartsWith("I"))
                        && c.Dependencies.Count >= 2
                        && c.Methods.Count >= 3)
            .OrderBy(c => Math.Abs(c.Methods.Count - 5))
            .FirstOrDefault();

        if (service != null)
        {
            md.Header(3, $"{result.ProjectName} — Service Pattern");
            md.CodeBlock(BuildServiceSkeleton(service), "csharp");
        }

        var entity = result.Classes
            .Where(c => c.Kind == "class"
                        && c.FilePath.Replace('\\', '/').Contains("/Entities/")
                        && c.Properties.Count >= 4
                        && c.Properties.Any(p => p.IsNavigation))
            .OrderBy(c => Math.Abs(c.Properties.Count - 8))
            .FirstOrDefault();

        if (entity != null)
        {
            md.Header(3, $"{result.ProjectName} — Entity Pattern");
            md.CodeBlock(BuildEntitySkeleton(entity), "csharp");
        }
    }

    private void GenerateFrontendPatterns(MarkdownBuilder md, AnalysisResult result)
    {
        var pageFile = result.ScannedFiles
            .Where(f => f.Replace('\\', '/').Contains("/pages/") && f.EndsWith(".tsx"))
            .OrderBy(f => new FileInfo(f).Length)
            .Skip(result.ScannedFiles.Count(f => f.Replace('\\', '/').Contains("/pages/")) / 3)
            .FirstOrDefault();

        if (pageFile == null) return;

        try
        {
            var content = File.ReadAllText(pageFile);
            var skeleton = ExtractReactSkeleton(content, Path.GetFileNameWithoutExtension(pageFile));
            if (!string.IsNullOrWhiteSpace(skeleton))
            {
                md.Header(3, $"{result.ProjectName} — Page Pattern");
                md.CodeBlock(skeleton, "tsx");
            }
        }
        catch { }
    }

    private string BuildControllerSkeleton(ClassInfo cls)
    {
        var sb = new StringBuilder();
        var noiseDeps = new HashSet<string> { "ILogger", "IConfiguration", "IWebHostEnvironment" };

        if (cls.Attributes.Contains("Authorize"))
            sb.AppendLine("[Authorize]");
        sb.AppendLine("[ApiController]");
        if (cls.RouteTemplate != null)
            sb.AppendLine($"[Route(\"{cls.RouteTemplate}\")]");

        var basePart = cls.BaseType != null ? $" : {cls.BaseType}" : " : ControllerBase";
        sb.AppendLine($"public class {cls.Name}{basePart}");
        sb.AppendLine("{");

        var deps = cls.Dependencies.Where(d => !noiseDeps.Contains(d)).ToList();
        foreach (var dep in deps)
        {
            var fieldName = "_" + char.ToLower(dep.TrimStart('I')[0]) + dep.TrimStart('I')[1..];
            sb.AppendLine($"    private readonly {dep} {fieldName};");
        }

        if (deps.Count > 0) sb.AppendLine();

        var endpoints = cls.Methods.Where(m => m.HttpMethod != null).Take(3).ToList();
        foreach (var method in endpoints)
        {
            var httpAttr = $"[Http{Capitalize(method.HttpMethod!)}";
            if (!string.IsNullOrEmpty(method.RoutePath))
                httpAttr += $"(\"{method.RoutePath}\")";
            httpAttr += "]";
            sb.AppendLine($"    {httpAttr}");

            var asyncPrefix = method.IsAsync ? "async " : "";
            var parameters = string.IsNullOrEmpty(method.Parameters) ? "" : method.Parameters;
            sb.AppendLine($"    public {asyncPrefix}{method.ReturnType} {method.Name}({parameters}) {{ ... }}");
            sb.AppendLine();
        }

        var remaining = cls.Methods.Count(m => m.HttpMethod != null) - 3;
        if (remaining > 0)
            sb.AppendLine($"    // ... +{remaining} more endpoints");

        sb.AppendLine("}");
        return sb.ToString().TrimEnd();
    }

    private string BuildServiceSkeleton(ClassInfo cls)
    {
        var sb = new StringBuilder();
        var iface = cls.Interfaces.FirstOrDefault(i => i.StartsWith("I")) ?? "";
        var noiseDeps = new HashSet<string> { "ILogger", "IConfiguration", "IWebHostEnvironment", "string", "int", "bool" };

        if (!string.IsNullOrEmpty(iface))
        {
            sb.AppendLine($"public interface {iface}");
            sb.AppendLine("{");
            foreach (var method in cls.Methods.Take(4))
                sb.AppendLine($"    {method.ReturnType} {method.Name}({method.Parameters});");
            var remaining = cls.Methods.Count - 4;
            if (remaining > 0)
                sb.AppendLine($"    // ... +{remaining} more methods");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine($"public class {cls.Name} : {iface}");
        sb.AppendLine("{");

        var deps = cls.Dependencies.Where(d => !noiseDeps.Contains(d)).ToList();
        foreach (var dep in deps)
        {
            var fieldName = "_" + char.ToLower(dep.TrimStart('I')[0]) + dep.TrimStart('I')[1..];
            sb.AppendLine($"    private readonly {dep} {fieldName};");
        }

        if (deps.Count > 0)
        {
            sb.AppendLine();
            var ctorParams = string.Join(", ", deps.Select(d =>
            {
                var paramName = char.ToLower(d.TrimStart('I')[0]) + d.TrimStart('I')[1..];
                return $"{d} {paramName}";
            }));
            sb.AppendLine($"    public {cls.Name}({ctorParams}) {{ ... }}");
            sb.AppendLine();
        }

        foreach (var method in cls.Methods.Take(3))
        {
            var asyncPrefix = method.IsAsync ? "async " : "";
            sb.AppendLine($"    public {asyncPrefix}{method.ReturnType} {method.Name}({method.Parameters}) {{ ... }}");
        }

        var moreCount = cls.Methods.Count - 3;
        if (moreCount > 0)
            sb.AppendLine($"    // ... +{moreCount} more methods");

        sb.AppendLine("}");
        return sb.ToString().TrimEnd();
    }

    private string BuildEntitySkeleton(ClassInfo cls)
    {
        var sb = new StringBuilder();
        var basePart = cls.BaseType != null ? $" : {cls.BaseType}" : "";

        sb.AppendLine($"public class {cls.Name}{basePart}");
        sb.AppendLine("{");

        var scalars = cls.Properties.Where(p => !p.IsNavigation).ToList();
        foreach (var prop in scalars.Take(6))
            sb.AppendLine($"    public {prop.Type} {prop.Name} {{ get; set; }}");
        var moreScalars = scalars.Count - 6;
        if (moreScalars > 0)
            sb.AppendLine($"    // ... +{moreScalars} more properties");

        var navs = cls.Properties.Where(p => p.IsNavigation).ToList();
        if (navs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    // Navigation properties");
            foreach (var prop in navs.Take(4))
                sb.AppendLine($"    public {prop.Type} {prop.Name} {{ get; set; }}");
            var moreNavs = navs.Count - 4;
            if (moreNavs > 0)
                sb.AppendLine($"    // ... +{moreNavs} more navigation properties");
        }

        sb.AppendLine("}");
        return sb.ToString().TrimEnd();
    }

    private string ExtractReactSkeleton(string content, string componentName)
    {
        var lines = content.Split('\n');
        var sb = new StringBuilder();

        var imports = lines
            .Where(l => l.TrimStart().StartsWith("import "))
            .Take(5)
            .ToList();

        foreach (var imp in imports)
            sb.AppendLine(imp.TrimEnd('\r'));

        if (imports.Count > 0)
        {
            var moreImports = lines.Count(l => l.TrimStart().StartsWith("import ")) - 5;
            if (moreImports > 0)
                sb.AppendLine($"// ... +{moreImports} more imports");
            sb.AppendLine();
        }

        var hooks = lines
            .Where(l =>
            {
                var trimmed = l.Trim();
                if (trimmed.StartsWith("import ")) return false;
                return trimmed.Contains("useState") || trimmed.Contains("useEffect")
                    || trimmed.Contains("useAuth") || trimmed.Contains("useParams")
                    || trimmed.Contains("useNavigate") || trimmed.Contains("useTheme");
            })
            .Take(5)
            .ToList();

        if (hooks.Count > 0)
        {
            sb.AppendLine($"export default function {componentName}() {{");
            foreach (var hook in hooks)
                sb.AppendLine("  " + hook.Trim().TrimEnd('\r'));
            sb.AppendLine();
            sb.AppendLine("  // ... component logic");
            sb.AppendLine();
            sb.AppendLine("  return ( <div>...</div> );");
            sb.AppendLine("}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLower();
}
