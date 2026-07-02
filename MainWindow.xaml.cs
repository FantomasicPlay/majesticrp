using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MajesticParser.ViewModels;

namespace MajesticParser;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
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
