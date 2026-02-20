namespace ClaudeCodeWin.ContextSnapshot.Models;

public class EndpointInfo
{
    public string HttpMethod { get; set; } = "";
    public string Path { get; set; } = "";
    public string ControllerName { get; set; } = "";
    public string ActionName { get; set; } = "";
    public bool RequiresAuth { get; set; }
}
