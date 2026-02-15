using System.IO;
using System.Text.RegularExpressions;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public partial class ConfigurationSection : ISnapshotSection
{
    public string Title => "Key Configuration";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        var programFiles = results
            .Where(r => r.ProjectType == "aspnet-core")
            .SelectMany(r => r.ScannedFiles.Where(f => Path.GetFileName(f) == "Program.cs"))
            .ToList();

        if (programFiles.Count == 0)
            return;

        md.Header(2, "10. Key Configuration");

        foreach (var programFile in programFiles)
        {
            try
            {
                var content = File.ReadAllText(programFile);
                var projectName = results
                    .First(r => r.ScannedFiles.Contains(programFile))
                    .ProjectName;

                var sections = new List<string>();

                var authProviders = ExtractAuthProviders(content);
                if (authProviders.Count > 0)
                    sections.Add($"**Auth:** {string.Join(", ", authProviders)}");

                if (content.Contains("AddCors") || content.Contains("UseCors"))
                    sections.Add("**CORS:** Enabled");

                var middleware = ExtractMiddleware(content);
                if (middleware.Count > 0)
                    sections.Add($"**Middleware pipeline:** {string.Join(" → ", middleware)}");

                if (content.Contains("UseNpgsql") || content.Contains("Npgsql"))
                    sections.Add("**Database:** PostgreSQL (Npgsql)");
                else if (content.Contains("UseSqlServer"))
                    sections.Add("**Database:** SQL Server");

                var external = ExtractExternalServices(content);
                if (external.Count > 0)
                    sections.Add($"**External services:** {string.Join(", ", external)}");

                if (sections.Count > 0)
                {
                    md.Header(3, projectName);
                    foreach (var section in sections)
                        md.Line(section);
                }
            }
            catch { }
        }
    }

    private List<string> ExtractAuthProviders(string content)
    {
        var providers = new List<string>();

        if (content.Contains("AddGoogle") || content.Contains("GoogleDefaults"))
            providers.Add("Google OAuth 2.0");
        if (content.Contains("AddJwtBearer") || content.Contains("JwtBearerDefaults"))
            providers.Add("JWT Bearer");
        if (content.Contains("AddCookie") || content.Contains("CookieAuthenticationDefaults"))
            providers.Add("Cookie");
        if (content.Contains("MagicLink", StringComparison.OrdinalIgnoreCase))
            providers.Add("Magic Links");
        if (content.Contains("AddIdentity") || content.Contains("AddDefaultIdentity"))
            providers.Add("ASP.NET Identity");

        return providers;
    }

    private List<string> ExtractMiddleware(string content)
    {
        var middleware = new List<string>();
        var lines = content.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var match = UseMiddleware().Match(trimmed);
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                if (name is "Routing" or "Endpoints" or "HttpsRedirection"
                    or "StaticFiles" or "DefaultFiles")
                    continue;
                middleware.Add(name);
            }
        }

        return middleware.Distinct().ToList();
    }

    private List<string> ExtractExternalServices(string content)
    {
        var services = new List<string>();

        if (content.Contains("AmazonS3") || content.Contains("AWSSDK.S3"))
            services.Add("AWS S3");
        if (content.Contains("AmazonSimpleEmailService") || content.Contains("AWSSDK.SimpleEmail"))
            services.Add("AWS SES");
        if (content.Contains("IHttpClientFactory") || content.Contains("AddHttpClient"))
            services.Add("HTTP clients");
        if (content.Contains("AddSignalR"))
            services.Add("SignalR");

        return services;
    }

    [GeneratedRegex(@"app\.Use(\w+)\s*\(", RegexOptions.Compiled)]
    private static partial Regex UseMiddleware();
}
