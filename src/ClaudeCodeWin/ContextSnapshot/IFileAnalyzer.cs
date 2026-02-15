using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot;

public interface IFileAnalyzer
{
    bool CanAnalyze(string filePath);
    void Analyze(string filePath, string content, AnalysisResult result);
}
