using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot;

public interface ISnapshotSection
{
    string Title { get; }
    void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath);
}
