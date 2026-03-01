using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Views;

public partial class TeamPanelControl : UserControl
{
    public TeamPanelControl()
    {
        InitializeComponent();
    }

    private TeamViewModel? VM => DataContext as TeamViewModel;

    private void Feature_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BacklogFeatureVM vm && vm.HasPhases)
        {
            vm.IsExpanded = !vm.IsExpanded;
            e.Handled = true;
        }
    }

    private void ContextMenu_DeleteFeature(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && GetFeatureFromContextMenu(mi) is { } vm)
            VM?.DeleteFeatureCommand.Execute(vm);
    }

    private void ContextMenu_CancelFeature(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && GetFeatureFromContextMenu(mi) is { } vm)
            VM?.CancelFeatureCommand.Execute(vm);
    }

    private void AddIdeaTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VM?.AddIdeaCommand.CanExecute(null) == true)
        {
            VM.AddIdeaCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void AnswerTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is BacklogFeatureVM vm)
        {
            VM?.AnswerQuestionCommand.Execute(vm);
            e.Handled = true;
        }
    }

    private void SendAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.AnswerQuestionCommand.Execute(vm);
    }

    // --- Analysis section handlers ---

    private void ApproveAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.ApproveAnalysisCommand.Execute(vm);
    }

    private void RejectAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.RejectAnalysisCommand.Execute(vm);
    }

    private void SendToPlanning_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.SendToPlanningCommand.Execute(vm);
    }

    private void AnalysisAnswerTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is BacklogFeatureVM vm)
        {
            VM?.AnswerAnalysisCommand.Execute(vm);
            e.Handled = true;
        }
    }

    private void SendAnalysisAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.AnswerAnalysisCommand.Execute(vm);
    }

    // --- Plan approval section handlers ---

    private void ApprovePlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.ApprovePlanCommand.Execute(vm);
    }

    private void RejectPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.RejectPlanCommand.Execute(vm);
    }

    private void RequestAIReview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.RequestAIReviewCommand.Execute(vm);
    }

    private void RetryPlanning_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.RetryPlanningCommand.Execute(vm);
    }

    // --- Backlog section handlers ---

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.AddToQueueCommand.Execute(vm);
    }

    private void ReturnToBacklog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.ReturnToBacklogCommand.Execute(vm);
    }

    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.ViewHistoryCommand.Execute(vm);
    }

    // --- Completed section handlers ---

    private void CommitFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.CommitFeatureCommand.Execute(vm);
    }

    private void ArchiveFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.ArchiveFeatureCommand.Execute(vm);
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm && vm.HasPhases)
            vm.IsExpanded = !vm.IsExpanded;
    }

    private void AskInChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.AskInChatCommand.Execute(vm);
    }

    private void UserActions_Click(object sender, MouseButtonEventArgs e)
    {
        // Tooltip shows on hover; click does nothing extra
        e.Handled = true;
    }

    private void ErrorExpand_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BacklogFeatureVM vm)
        {
            vm.IsErrorExpanded = !vm.IsErrorExpanded;
            e.Handled = true;
        }
    }

    private void LogHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (LogTextBox.Visibility == Visibility.Collapsed)
        {
            LogTextBox.Visibility = Visibility.Visible;
            LogArrow.Text = "\u25BE"; // down
        }
        else
        {
            LogTextBox.Visibility = Visibility.Collapsed;
            LogArrow.Text = "\u25B8"; // right
        }
        e.Handled = true;
    }

    private void ManagerHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ToggleManagerPanel();
        e.Handled = true;
    }

    private void ToggleManager_Click(object sender, RoutedEventArgs e)
    {
        ToggleManagerPanel();
    }

    private void ToggleManagerPanel()
    {
        if (ManagerPanel.Visibility == Visibility.Collapsed)
        {
            ManagerPanel.Visibility = Visibility.Visible;
            ManagerArrow.Text = "\u25BE"; // down
        }
        else
        {
            ManagerPanel.Visibility = Visibility.Collapsed;
            ManagerArrow.Text = "\u25B8"; // right
        }
    }

    private void ManagerInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VM?.SendManagerMessageCommand.CanExecute(null) == true)
        {
            VM.SendManagerMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static BacklogFeatureVM? GetFeatureFromContextMenu(MenuItem menuItem)
    {
        if (menuItem.Parent is ContextMenu ctx && ctx.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as BacklogFeatureVM;
        return null;
    }
}
