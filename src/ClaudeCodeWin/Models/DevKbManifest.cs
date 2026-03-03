namespace ClaudeCodeWin.Models;

public class DevKbManifest
{
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<DevKbArticle> Articles { get; set; } = [];
}

public class DevKbArticle
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string WhenToRead { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string ContentMd { get; set; } = string.Empty;
}
