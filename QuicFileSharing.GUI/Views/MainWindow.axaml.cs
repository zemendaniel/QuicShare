using System.Drawing;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace QuicFileSharing.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LayoutUpdated += (s, e) => UpdateMinSize();

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