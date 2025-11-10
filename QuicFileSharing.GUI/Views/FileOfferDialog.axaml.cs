using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuicFileSharing.GUI.ViewModels;

namespace QuicFileSharing.GUI.Views;

public partial class FileOfferDialog : Window
{
    public FileOfferDialog()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is FileOfferDialogViewModel vm)
                _ = HandleResultAsync(vm);
        };
        LayoutUpdated += (s, e) => UpdateMinSize();

    }

    private async Task HandleResultAsync(FileOfferDialogViewModel vm)
    {
        var (accepted, path) = await vm.ResultTask;
        Close((accepted, path));
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
    
    private void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}