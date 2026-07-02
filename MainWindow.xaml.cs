using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MajesticParser.ViewModels;

namespace MajesticParser;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private TreeNodeViewModel? _selectAnchor;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }

    // ===== мультивыделение узлов дерева (Ctrl/Shift) =====

    private static TreeNodeViewModel? NodeFrom(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not TreeViewItem)
            d = VisualTreeHelper.GetParent(d);
        return (d as TreeViewItem)?.DataContext as TreeNodeViewModel;
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        var node = NodeFrom(e.OriginalSource);

        if (!ctrl && !shift)
        {
            // обычный клик — сбрасываем выделение (галочки/разворачивание работают как обычно)
            _vm.ClearSelection();
            _selectAnchor = node;
            return;
        }

        if (node == null)
            return;

        e.Handled = true; // модификатор-клик не меняет обычное выделение дерева
        if (ctrl)
        {
            _vm.ToggleSelect(node);
            _selectAnchor = node;
        }
        else if (shift)
        {
            _vm.SelectRange(_selectAnchor ?? node, node);
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            _vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // При развороте WindowChrome обрезает края — компенсируем отступом
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);

        // ❐ = restore, □ = maximize (□)
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Shutdown();
        base.OnClosing(e);
    }
}
