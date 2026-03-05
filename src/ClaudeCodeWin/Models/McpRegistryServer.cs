namespace ClaudeCodeWin.Models;

public class McpRegistryServer
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string? WebsiteUrl { get; set; }
    public McpRepository? Repository { get; set; }
    public List<McpPackage> Packages { get; set; } = [];
    public List<McpRemote> Remotes { get; set; } = [];

    // Fix: null-safe — Name/Packages/Remotes can be null when deserialized from API JSON
    public string DisplayName => string.IsNullOrEmpty(Name) ? ""
        : Name.Contains('/') ? Name[(Name.LastIndexOf('/') + 1)..] : Name;

    public string AuthorDisplay => string.IsNullOrEmpty(Name) ? "unknown"
        : Name.Contains('/') ? Name[..Name.LastIndexOf('/')] : "unknown";

    public string TransportDisplay
    {
        get
        {
            if (Remotes is { Count: > 0 }) return Remotes[0].Type;
            if (Packages is { Count: > 0 }) return Packages[0].Transport?.Type ?? "stdio";
            return "";
        }
    }

    public string PackageTypeDisplay
    {
        get
        {
            if (Packages is { Count: > 0 }) return Packages[0].RegistryType;
            if (Remotes is { Count: > 0 }) return "remote";
            return "";
        }
    }

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            var pkg = PackageTypeDisplay;
            if (!string.IsNullOrEmpty(pkg)) parts.Add(pkg);
            var transport = TransportDisplay;
            if (!string.IsNullOrEmpty(transport)) parts.Add(transport);
            parts.Add(AuthorDisplay);
            return string.Join(" · ", parts);
        }
    }
}

public class McpRepository
{
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
}

public class McpPackage
{
    public string RegistryType { get; set; } = "";
    public string Identifier { get; set; } = "";
    public McpTransport? Transport { get; set; }
    public List<McpEnvVar>? EnvironmentVariables { get; set; }
}

public class McpTransport
{
    public string Type { get; set; } = "stdio";
}

public class McpRemote
{
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
}

public class McpEnvVar
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsSecret { get; set; }
}
