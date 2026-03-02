using System.Windows.Controls;
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

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
