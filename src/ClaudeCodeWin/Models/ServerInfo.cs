namespace ClaudeCodeWin.Models;

public class ServerInfo
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "root";
    public string? Description { get; set; }
    public List<string> Projects { get; set; } = [];
}
