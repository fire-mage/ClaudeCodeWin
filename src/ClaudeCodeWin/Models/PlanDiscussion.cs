namespace ClaudeCodeWin.Models;

public class PlanDiscussionQuestion
{
    public int Index { get; set; }
    public string Question { get; set; } = "";
    public List<string> SuggestedAnswers { get; set; } = [];
}

public class PlanDiscussionResult
{
    public List<PlanDiscussionQuestion> Questions { get; set; } = [];
}
