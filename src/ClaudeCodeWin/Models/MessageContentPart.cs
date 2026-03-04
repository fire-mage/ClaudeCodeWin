namespace ClaudeCodeWin.Models;

public enum MessageContentType { Text, Image }

public class MessageContentPart
{
    public MessageContentType ContentType { get; }
    public string? Text { get; }
    public FileAttachment? Attachment { get; }

    private MessageContentPart(MessageContentType type, string? text, FileAttachment? attachment)
    {
        ContentType = type;
        Text = text;
        Attachment = attachment;
    }

    public static MessageContentPart CreateText(string text) => new(MessageContentType.Text, text, null);
    public static MessageContentPart CreateImage(FileAttachment attachment) => new(MessageContentType.Image, null, attachment);
}
