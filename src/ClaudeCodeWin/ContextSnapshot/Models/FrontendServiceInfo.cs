namespace ClaudeCodeWin.ContextSnapshot.Models;

public class FrontendServiceInfo
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public List<ApiCallInfo> ApiCalls { get; set; } = [];
    public List<string> Exports { get; set; } = [];
}

public class ApiCallInfo
{
    public string HttpMethod { get; set; } = "";
    public string Path { get; set; } = "";
}
