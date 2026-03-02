using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Views;

public partial class TeamPanelControl : UserControl
{
    private TeamViewModel? _subscribedVm;

    public TeamPanelControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = DataContext as TeamViewModel;
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TeamViewModel.LiveChatText))
        {
            // Auto-scroll if already near bottom
            Dispatcher.InvokeAsync(() =>
            {
                if (ChatScrollViewer == null) return;
                var isNearBottom = ChatScrollViewer.VerticalOffset >=
                    ChatScrollViewer.ScrollableHeight - 20;
                if (isNearBottom)
                    ChatScrollViewer.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
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

    private void DeleteFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.DeleteFeatureCommand.Execute(vm);
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

    // --- Discussion section handlers ---

    private void DiscussPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            VM?.DiscussPlanCommand.Execute(vm);
    }

    private void SubmitDiscussion_Click(object sender, RoutedEventArgs e)
    {
        // The button inherits DataContext from the outer DataTemplate (BacklogFeatureVM)
        if (sender is FrameworkElement fe && FindParentFeatureVM(fe) is { } vm)
            VM?.SubmitDiscussionCommand.Execute(vm);
    }

    private void CancelDiscussion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && FindParentFeatureVM(fe) is { } vm)
            VM?.CancelDiscussionCommand.Execute(vm);
    }

    private static BacklogFeatureVM? FindParentFeatureVM(FrameworkElement element)
    {
        var fe = element;
        while (fe != null)
        {
            if (fe.DataContext is BacklogFeatureVM vm)
                return vm;
            fe = System.Windows.Media.VisualTreeHelper.GetParent(fe) as FrameworkElement;
        }
        return null;
    }

    private void DiscussionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb)
        {
            // The Tag holds the DiscussionQuestionVM (set in XAML)
            if (rb.Tag is DiscussionQuestionVM questionVm)
            {
                questionVm.SelectedAnswer = rb.Content as string ?? "";
                questionVm.IsCustom = false;
            }
        }
    }

    private void DiscussionCustomText_GotFocus(object sender, RoutedEventArgs e)
    {
        // When user clicks into the custom text box, auto-select the "Other" radio
        if (sender is TextBox tb && tb.DataContext is DiscussionQuestionVM questionVm)
        {
            questionVm.IsCustom = true;
        }
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

    private void QuickAddIdea_Click(object sender, RoutedEventArgs e)
    {
        if (VM != null && VM.HasProjectLoaded && !string.IsNullOrWhiteSpace(QuickIdeaBox.Text))
        {
            var text = QuickIdeaBox.Text.Trim();
            VM.IdeasText = string.IsNullOrEmpty(VM.IdeasText)
                ? text
                : VM.IdeasText.TrimEnd() + "\n" + text;
            QuickIdeaBox.Text = "";
        }
    }

    private void QuickIdeaBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VM is { HasProjectLoaded: true }
            && !string.IsNullOrWhiteSpace(QuickIdeaBox.Text))
        {
            QuickAddIdea_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ViewChat_Click(object sender, RoutedEventArgs e)
    {
        if (VM != null)
            VM.IsDevChatVisible = !VM.IsDevChatVisible;
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

    private static BacklogFeatureVM? GetFeatureFromContextMenu(MenuItem menuItem)
    {
        if (menuItem.Parent is ContextMenu ctx && ctx.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as BacklogFeatureVM;
        return null;
    }
}
