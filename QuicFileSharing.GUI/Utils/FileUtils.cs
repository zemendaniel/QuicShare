using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform.Storage;

namespace QuicFileSharing.GUI.Utils;

public static class FileUtils
{
    public static bool CanWriteToFolder(string folderPath, long requiredBytes)
    {
        if (!Directory.Exists(folderPath))
            return false;

        try
        {
            var testFile = Path.Combine(folderPath, Path.GetRandomFileName());
            using (var _ = File.Create(testFile, 1, FileOptions.DeleteOnClose)) {}
            var root = Path.GetPathRoot(folderPath)!;
            var drive = new DriveInfo(root);
            var freeBytes = drive.AvailableFreeSpace;

            return freeBytes >= requiredBytes;
        }
        catch
        {
            return false;
        }
    }
    
    public static bool CanReadFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;
        try
        {
            using (var _ = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {}
            return true;
        }
        catch
        {
            return false;
        }
    }
    public static string? ResolveFolderPath(IStorageFolder folder)
    {
        if (folder.Path is not { IsAbsoluteUri: true, Scheme: "file" })
            return null;
        
        var path = Uri.UnescapeDataString(folder.Path.LocalPath);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return path;
        
        path = path.Replace('/', '\\');
        if (folder.Path.Host != "")
            path = $@"\\{folder.Path.Host}{path[1..]}";

        return path;
    }


}