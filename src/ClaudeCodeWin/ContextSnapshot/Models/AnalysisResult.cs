namespace ClaudeCodeWin.ContextSnapshot.Models;

public class AnalysisResult
{
    public string ProjectName { get; set; } = "";
    public string ProjectType { get; set; } = "";
    public List<ClassInfo> Classes { get; set; } = [];
    public List<EndpointInfo> Endpoints { get; set; } = [];
    public List<RouteInfo> Routes { get; set; } = [];
    public List<FrontendServiceInfo> FrontendServices { get; set; } = [];
    public List<DependencyEdge> Dependencies { get; set; } = [];
    public List<string> ScannedFiles { get; set; } = [];
}
