namespace ClaudeCodeWin.ViewModels;

/// <summary>
/// Review partial — all review logic now lives in ChatSessionViewModel.
/// This file is kept for the CancelReview delegate used by conflict handling.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Cancel active review on the current chat session.</summary>
    private void CancelReview() => ActiveChatSession?.CancelReview();
}
