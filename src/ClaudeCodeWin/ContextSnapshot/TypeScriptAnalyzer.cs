using System.IO;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot;

public class TypeScriptAnalyzer : IFileAnalyzer
{
    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    public void Analyze(string filePath, string content, AnalysisResult result)
    {
        var fileName = Path.GetFileName(filePath);

        if (fileName.Equals("App.tsx", StringComparison.OrdinalIgnoreCase))
            ExtractRoutes(content, result);

        if (filePath.Replace('\\', '/').Contains("/services/"))
            ExtractServiceInfo(filePath, content, result);

        if (filePath.Replace('\\', '/').Contains("/pages/") ||
            filePath.Replace('\\', '/').Contains("/components/"))
            ExtractExports(filePath, content, result);
    }

    private void ExtractRoutes(string content, AnalysisResult result)
    {
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var match = RegexPatterns.ReactRoute().Match(line);
            if (match.Success)
            {
                var path = match.Groups[1].Value;
                var elementExpr = match.Groups[2].Value;

                var componentMatch = RegexPatterns.JsxComponent().Match(elementExpr);
                var component = "";
                if (componentMatch.Success)
                {
                    var name = componentMatch.Value.TrimStart('<').TrimEnd('/', '>').Trim();
                    if (name != "Navigate")
                        component = name;
                    else
                    {
                        var allComponents = RegexPatterns.JsxComponent().Matches(elementExpr);
                        foreach (System.Text.RegularExpressions.Match cm in allComponents)
                        {
                            var n = cm.Value.TrimStart('<').TrimEnd('/', '>').Trim();
                            if (n != "Navigate")
                            {
                                component = n;
                                break;
                            }
                        }
                    }
                }

                var requiresAuth = elementExpr.Contains("isAuthenticated") &&
                                   elementExpr.Contains("Navigate to=\"/login\"");

                result.Routes.Add(new RouteInfo
                {
                    Path = path,
                    Component = component,
                    RequiresAuth = requiresAuth
                });
            }
        }
    }

    private void ExtractServiceInfo(string filePath, string content, AnalysisResult result)
    {
        var serviceName = Path.GetFileNameWithoutExtension(filePath);
        var service = new FrontendServiceInfo
        {
            Name = serviceName,
            FilePath = filePath
        };

        var apiMatches = RegexPatterns.ApiCall().Matches(content);
        foreach (System.Text.RegularExpressions.Match match in apiMatches)
        {
            service.ApiCalls.Add(new ApiCallInfo
            {
                HttpMethod = match.Groups[1].Value.ToUpper(),
                Path = match.Groups[2].Value
            });
        }

        var exportMatches = RegexPatterns.TypeScriptExport().Matches(content);
        foreach (System.Text.RegularExpressions.Match match in exportMatches)
            service.Exports.Add(match.Groups[2].Value);

        if (service.ApiCalls.Count > 0 || service.Exports.Count > 0)
            result.FrontendServices.Add(service);
    }

    private void ExtractExports(string filePath, string content, AnalysisResult result)
    {
        var exportMatches = RegexPatterns.TypeScriptExport().Matches(content);
        if (exportMatches.Count == 0)
            return;

        var cls = new ClassInfo
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            Kind = "component",
            FilePath = filePath
        };

        foreach (System.Text.RegularExpressions.Match match in exportMatches)
        {
            cls.Methods.Add(new SnapshotMethodInfo
            {
                Name = match.Groups[2].Value,
                ReturnType = match.Groups[1].Value
            });
        }

        result.Classes.Add(cls);
    }
}
