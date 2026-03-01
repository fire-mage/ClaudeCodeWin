namespace ClaudeCodeWin.Models;

public enum ManagerActionType { Reprioritize, Cancel, Suggest }

public class ManagerAction
{
    public ManagerActionType Type { get; set; }
    public string? FeatureId { get; set; }
    public int? NewPriority { get; set; }
    public string? Reason { get; set; }
}
