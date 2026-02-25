namespace ClaudeCodeWin.Models;

public class MarketplacePlugin
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = "community";
    public List<string> Tags { get; set; } = [];
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a built-in plugin (true) or user-imported (false).
    /// </summary>
    public bool IsBuiltIn { get; set; }

    public string TagsDisplay => Tags.Count > 0 ? string.Join(", ", Tags) : "";
}
