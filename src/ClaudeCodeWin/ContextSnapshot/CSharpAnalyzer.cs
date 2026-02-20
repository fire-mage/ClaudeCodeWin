using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot;

public class CSharpAnalyzer : IFileAnalyzer
{
    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public void Analyze(string filePath, string content, AnalysisResult result)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var classes = ExtractClasses(lines, filePath);

        foreach (var cls in classes)
        {
            result.Classes.Add(cls);

            if (cls.Attributes.Contains("ApiController") || cls.Name.EndsWith("Controller"))
            {
                var endpoints = BuildEndpoints(cls);
                result.Endpoints.AddRange(endpoints);
            }

            foreach (var dep in cls.Dependencies)
            {
                result.Dependencies.Add(new DependencyEdge
                {
                    From = cls.Name,
                    To = dep
                });
            }
        }
    }

    private List<ClassInfo> ExtractClasses(string[] lines, string filePath)
    {
        var classes = new List<ClassInfo>();
        var pendingAttributes = new List<string>();
        string? pendingRoute = null;
        bool hasAuthorize = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            var routeMatch = RegexPatterns.RouteAttribute().Match(line);
            if (routeMatch.Success)
            {
                pendingRoute = routeMatch.Groups[1].Value;
                continue;
            }

            if (RegexPatterns.ApiControllerAttribute().IsMatch(line))
            {
                pendingAttributes.Add("ApiController");
                continue;
            }

            if (RegexPatterns.AuthorizeAttribute().IsMatch(line))
            {
                hasAuthorize = true;
                pendingAttributes.Add("Authorize");
                continue;
            }

            if (RegexPatterns.AllowAnonymousAttribute().IsMatch(line))
            {
                pendingAttributes.Add("AllowAnonymous");
                continue;
            }

            var classMatch = RegexPatterns.CSharpClassDeclaration().Match(line);
            if (classMatch.Success)
            {
                var cls = new ClassInfo
                {
                    Name = classMatch.Groups[5].Value,
                    Kind = classMatch.Groups[4].Value,
                    FilePath = filePath,
                    Attributes = new List<string>(pendingAttributes),
                    RouteTemplate = pendingRoute
                };

                var inheritance = classMatch.Groups[6].Value;
                if (!string.IsNullOrWhiteSpace(inheritance))
                {
                    var parts = inheritance.Split(',').Select(p => p.Trim()).ToList();
                    foreach (var part in parts)
                    {
                        var typeName = part.Split('<')[0].Trim();
                        if (parts.IndexOf(part) == 0 && !typeName.StartsWith('I'))
                            cls.BaseType = typeName;
                        else
                            cls.Interfaces.Add(typeName);
                    }
                }

                ExtractMembers(lines, i + 1, cls, hasAuthorize);
                classes.Add(cls);

                pendingAttributes.Clear();
                pendingRoute = null;
                hasAuthorize = false;
            }
        }

        return classes;
    }

    private void ExtractMembers(string[] lines, int startLine, ClassInfo cls, bool classHasAuthorize)
    {
        int braceDepth = 0;
        bool inClass = false;
        var pendingAttributes = new List<string>();
        string? pendingHttpMethod = null;
        string? pendingHttpRoute = null;

        for (int i = startLine; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            foreach (var ch in line)
            {
                if (ch == '{') braceDepth++;
                if (ch == '}') braceDepth--;
            }

            if (line.Contains('{') && !inClass)
                inClass = true;

            if (braceDepth <= 0 && inClass)
                break;

            if (braceDepth > 1 && !line.StartsWith('['))
                continue;

            var depMatch = RegexPatterns.ConstructorDependency().Match(line);
            if (depMatch.Success)
            {
                var typeName = depMatch.Groups[1].Value.Trim().Split('<')[0];
                cls.Dependencies.Add(typeName);
                continue;
            }

            var propMatch = RegexPatterns.PropertyDeclaration().Match(line);
            if (propMatch.Success)
            {
                var isVirtual = !string.IsNullOrEmpty(propMatch.Groups[1].Value);
                var propType = propMatch.Groups[3].Value;
                var propName = propMatch.Groups[4].Value;
                var isCollection = RegexPatterns.NavigationCollection().IsMatch(line);

                cls.Properties.Add(new SnapshotPropertyInfo
                {
                    Name = propName,
                    Type = propType,
                    IsNavigation = isVirtual || isCollection,
                    IsCollection = isCollection
                });
                continue;
            }

            var httpMatch = RegexPatterns.HttpMethodAttribute().Match(line);
            if (httpMatch.Success)
            {
                pendingHttpMethod = httpMatch.Groups[1].Value.ToUpper();
                pendingHttpRoute = httpMatch.Groups[2].Value;
                pendingAttributes.Add($"Http{httpMatch.Groups[1].Value}");
                continue;
            }

            if (RegexPatterns.AuthorizeAttribute().IsMatch(line))
            {
                pendingAttributes.Add("Authorize");
                continue;
            }

            if (RegexPatterns.AllowAnonymousAttribute().IsMatch(line))
            {
                pendingAttributes.Add("AllowAnonymous");
                continue;
            }

            var lineForMethod = line;
            if (line.Contains('(') && !line.Contains(')'))
            {
                var joined = line;
                for (int j = i + 1; j < lines.Length && j < i + 15; j++)
                {
                    joined += " " + lines[j].Trim();
                    if (lines[j].Contains(')'))
                        break;
                }
                lineForMethod = joined;
            }
            var methodMatch = RegexPatterns.MethodDeclaration().Match(lineForMethod);
            if (methodMatch.Success && braceDepth <= 1)
            {
                var isAsync = !string.IsNullOrEmpty(methodMatch.Groups[2].Value);
                var returnType = methodMatch.Groups[4].Value.Trim();
                var methodName = methodMatch.Groups[5].Value;
                var parameters = methodMatch.Groups[6].Value.Trim();

                if (methodName == cls.Name)
                {
                    pendingAttributes.Clear();
                    pendingHttpMethod = null;
                    pendingHttpRoute = null;
                    continue;
                }

                var method = new SnapshotMethodInfo
                {
                    Name = methodName,
                    ReturnType = returnType,
                    Parameters = parameters,
                    Attributes = new List<string>(pendingAttributes),
                    HttpMethod = pendingHttpMethod,
                    RoutePath = pendingHttpRoute,
                    IsAsync = isAsync
                };

                cls.Methods.Add(method);

                pendingAttributes.Clear();
                pendingHttpMethod = null;
                pendingHttpRoute = null;
            }
        }
    }

    private List<EndpointInfo> BuildEndpoints(ClassInfo cls)
    {
        var endpoints = new List<EndpointInfo>();
        var baseRoute = cls.RouteTemplate ?? "";
        var controllerName = cls.Name.Replace("Controller", "");
        bool classHasAuth = cls.Attributes.Contains("Authorize");

        foreach (var method in cls.Methods)
        {
            if (method.HttpMethod == null)
                continue;

            var path = BuildFullPath(baseRoute, method.RoutePath, controllerName);
            var requiresAuth = classHasAuth && !method.Attributes.Contains("AllowAnonymous");

            endpoints.Add(new EndpointInfo
            {
                HttpMethod = method.HttpMethod,
                Path = path,
                ControllerName = cls.Name,
                ActionName = method.Name,
                RequiresAuth = requiresAuth
            });
        }

        return endpoints;
    }

    private string BuildFullPath(string baseRoute, string? methodRoute, string controllerName)
    {
        var path = baseRoute.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(methodRoute))
        {
            if (!path.EndsWith('/') && !methodRoute.StartsWith('/'))
                path += "/";
            path += methodRoute;
        }

        if (!path.StartsWith('/'))
            path = "/" + path;

        return path;
    }
}
