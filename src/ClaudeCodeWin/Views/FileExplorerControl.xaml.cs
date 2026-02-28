using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Views;

public partial class FileExplorerControl : UserControl
{
    public FileExplorerControl()
    {
        InitializeComponent();
        FileTree.MouseDoubleClick += FileTree_MouseDoubleClick;
    }

    private ExplorerViewModel? VM => DataContext as ExplorerViewModel;

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (VM != null && e.NewValue is FileNode node)
            VM.SelectedNode = node;
    }

    private void FileTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VM?.SelectedNode is { IsDirectory: false, IsPlaceholder: false } node)
        {
            VM.OpenFileCommand.Execute(node);
            e.Handled = true;
        }
    }

    // Context menu handlers
    private void ContextMenu_Open(object sender, RoutedEventArgs e) => VM?.OpenFileCommand.Execute(null);
    private void ContextMenu_NewFile(object sender, RoutedEventArgs e) => VM?.NewFileCommand.Execute(null);
    private void ContextMenu_NewFolder(object sender, RoutedEventArgs e) => VM?.NewFolderCommand.Execute(null);
    private void ContextMenu_Rename(object sender, RoutedEventArgs e) => VM?.RenameCommand.Execute(null);
    private void ContextMenu_Delete(object sender, RoutedEventArgs e) => VM?.DeleteCommand.Execute(null);
    private void ContextMenu_CopyPath(object sender, RoutedEventArgs e) => VM?.CopyPathCommand.Execute(null);
    private void ContextMenu_RevealInExplorer(object sender, RoutedEventArgs e) => VM?.RevealInExplorerCommand.Execute(null);
}
