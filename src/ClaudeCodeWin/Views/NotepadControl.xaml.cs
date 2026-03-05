using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Views;

public partial class NotepadControl : UserControl
{
    public NotepadControl()
    {
        InitializeComponent();
    }

    private void NoteMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void NoteContextMenu_Rename(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotepadViewModel vm) return;
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is Button btn && btn.DataContext is string noteName)
            vm.SelectedNote = noteName;
        else
            return;
        if (vm.RenameNoteCommand.CanExecute(null))
        {
            vm.RenameNoteCommand.Execute(null);
            Dispatcher.BeginInvoke(() =>
            {
                RenameTextBox.Focus();
                RenameTextBox.SelectAll();
            });
        }
    }

    private void NoteContextMenu_Delete(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotepadViewModel vm) return;
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is Button btn && btn.DataContext is string noteName)
            vm.SelectedNote = noteName;
        else
            return;
        if (vm.DeleteNoteCommand.CanExecute(null))
            vm.DeleteNoteCommand.Execute(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && DataContext is NotepadViewModel vmSave)
        {
            if (vmSave.SaveNoteCommand.CanExecute(null))
                vmSave.SaveNoteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 && DataContext is NotepadViewModel vm)
        {
            if (vm.RenameNoteCommand.CanExecute(null))
            {
                vm.RenameNoteCommand.Execute(null);
                // Defer focus — WPF needs a layout pass to make the rename toolbar visible
                Dispatcher.BeginInvoke(() =>
                {
                    RenameTextBox.Focus();
                    RenameTextBox.SelectAll();
                });
                e.Handled = true;
            }
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not NotepadViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }
}
