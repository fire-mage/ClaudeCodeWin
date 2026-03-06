using System.Text.Json.Serialization;

namespace ClaudeCodeWin.Models;

public enum NoteBlockType { Text, Image }

public class NoteBlock
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NoteBlockType Type { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>Image filename relative to the notepad/images/ directory.</summary>
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageFile { get; set; }

    public static NoteBlock CreateText(string text) => new() { Type = NoteBlockType.Text, Text = text };
    public static NoteBlock CreateImage(string imageFile) => new() { Type = NoteBlockType.Image, ImageFile = imageFile };
}
