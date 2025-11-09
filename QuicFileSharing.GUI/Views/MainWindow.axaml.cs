using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuicFileSharing.GUI.ViewModels;

namespace QuicFileSharing.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.CheckQuicSupportAsync(this);
        };

    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButtonClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }
    private void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}