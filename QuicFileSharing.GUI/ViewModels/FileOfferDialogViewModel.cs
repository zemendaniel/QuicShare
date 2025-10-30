using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using QuicFileSharing.GUI.Utils;

namespace QuicFileSharing.GUI.ViewModels;

public partial class FileOfferDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string message = string.Empty;
    [ObservableProperty]
    private string savePath = string.Empty;
    [ObservableProperty]
    private string errorText = string.Empty;

    private readonly TaskCompletionSource<(bool accepted, string? path)> tcs = new();

    public Task<(bool accepted, string? path)> ResultTask => tcs.Task;
    private readonly long fileSize;

    public FileOfferDialogViewModel(string fileName, long fileSize, string savePath)
    {
        this.fileSize = fileSize;
        Message = $"Incoming file: {fileName} ({FormatBytes(fileSize)})";
        SavePath = savePath;
    }
    [RelayCommand]
    private async Task SelectFolder(Window window)
    {
        ErrorText = string.Empty;
        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(SavePath) &&
            Directory.Exists(SavePath))
        {
            startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(SavePath);
        }
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder to save the file",
            SuggestedStartLocation = startLocation
        });

        if (folders.Count == 0)
            return;
        
        var folderPath = FileUtils.ResolveFolderPath(folders[0]);
        if (folderPath is null)
        {
            ErrorText = $"Could not resolve {folderPath}. Default path will be used or chose another folder.";
            SavePath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); 
            return;
        }

        if (!FileUtils.CanWriteToFolder(folderPath, fileSize))
        {
            ErrorText = $"Cannot write to {folderPath}. Make sure permissions are correct" +
                        " and you have enough space. Default path will be used or chose another folder.";
            SavePath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); 
            return;       
        }
            
        SavePath = folderPath;
    }

    [RelayCommand]
    private void Accept()
    {
        tcs.SetResult((true, SavePath));
    }

    [RelayCommand]
    private void Reject()
    {
        tcs.SetResult((false, null));
    }
}