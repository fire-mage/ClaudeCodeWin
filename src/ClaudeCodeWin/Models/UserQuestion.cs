namespace ClaudeCodeWin.Models;

public class UserQuestion
{
    public string Question { get; set; } = "";
    public string Header { get; set; } = "";
    public List<QuestionOption> Options { get; set; } = [];
    public bool MultiSelect { get; set; }
}

public class QuestionOption
{
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
}
