using System.Text.Json.Serialization;

namespace ClaudeCodeWin.Models;

public class OnboardingResult
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("claudeMd")]
    public string? ClaudeMdContent { get; set; }

    [JsonPropertyName("scripts")]
    public List<OnboardingScript> Scripts { get; set; } = [];

    [JsonPropertyName("tasks")]
    public List<OnboardingTask> Tasks { get; set; } = [];

    [JsonPropertyName("kbArticles")]
    public List<OnboardingKbArticle> KbArticles { get; set; } = [];
}

public class OnboardingScript
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("hotKey")]
    public string? HotKey { get; set; }
}

public class OnboardingTask
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("confirmBeforeRun")]
    public bool ConfirmBeforeRun { get; set; } = true;
}

public class OnboardingKbArticle
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("whenToRead")]
    public string WhenToRead { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}
