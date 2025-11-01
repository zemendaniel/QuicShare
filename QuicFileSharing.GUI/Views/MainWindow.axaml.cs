using Avalonia.Controls;
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
}