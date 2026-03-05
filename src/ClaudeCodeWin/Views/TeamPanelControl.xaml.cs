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
        Unloaded += (_, _) =>
        {
            _teamChatWindow?.Close();
            _teamChatWindow = null;
        };
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

    private void ViewChat_Feature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BacklogFeatureVM vm)
            OpenChatForFeature(vm);
    }

    private async void OpenChatForFeature(BacklogFeatureVM vm)
    {
        if (VM == null) return;

        // Open or reuse the TeamChatWindow first (user sees it immediately)
        if (_teamChatWindow is not null && _teamChatWindow.IsLoaded)
        {
            _teamChatWindow.Activate();
        }
        else
        {
            _teamChatWindow = new TeamChatWindow
            {
                DataContext = VM,
                Owner = System.Windows.Window.GetWindow(this)
            };
            _teamChatWindow.Closed += (_, _) => _teamChatWindow = null;
            _teamChatWindow.Show();
        }

        // Load session history (async — chat populates after loading)
        try
        {
            await VM.LoadSessionHistoryChatAsync(vm.Feature);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadSessionHistoryChat failed: {ex.Message}");
        }
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

    private TeamChatWindow? _teamChatWindow;

    private void ViewChat_Click(object sender, RoutedEventArgs e)
    {
        if (VM == null) return;

        // If the window is already open, just bring it to front
        if (_teamChatWindow is not null && _teamChatWindow.IsLoaded)
        {
            _teamChatWindow.Activate();
            return;
        }

        _teamChatWindow = new TeamChatWindow
        {
            DataContext = VM,
            Owner = System.Windows.Window.GetWindow(this)
        };
        _teamChatWindow.Closed += (_, _) => _teamChatWindow = null;
        _teamChatWindow.Show();
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
