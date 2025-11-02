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
        LayoutUpdated += (s, e) => UpdateMinSize();

        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.CheckQuicSupportAsync(this);
        };

    }
    private void UpdateMinSize()
    {
        if (Content is Control content)
        {
            content.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = content.DesiredSize;

            MinWidth = desired.Width;
            MinHeight = desired.Height;
        }
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