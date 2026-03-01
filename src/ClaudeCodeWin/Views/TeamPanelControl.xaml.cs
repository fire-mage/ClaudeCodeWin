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

    private void LogHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (LogTextBox.Visibility == Visibility.Collapsed)
        {
            LogTextBox.Visibility = Visibility.Visible;
            LogArrow.Text = "\u25BE"; // ▾
        }
        else
        {
            LogTextBox.Visibility = Visibility.Collapsed;
            LogArrow.Text = "\u25B8"; // ▸
        }
        e.Handled = true;
    }

    private void ManagerHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (ManagerPanel.Visibility == Visibility.Collapsed)
        {
            ManagerPanel.Visibility = Visibility.Visible;
            ManagerArrow.Text = "\u25BE"; // ▾
        }
        else
        {
            ManagerPanel.Visibility = Visibility.Collapsed;
            ManagerArrow.Text = "\u25B8"; // ▸
        }
        e.Handled = true;
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
        // Walk up: MenuItem → ContextMenu → PlacementTarget (Border) → DataContext
        if (menuItem.Parent is ContextMenu ctx && ctx.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as BacklogFeatureVM;
        return null;
    }
}
