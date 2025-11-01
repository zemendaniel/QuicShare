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
        return ResolvePath(new Uri(folder.Path.ToString(), UriKind.Absolute));
    }
    public static string? ResolveFilePath(IStorageFile file)
    {
        return ResolvePath(new Uri(file.Path.ToString(), UriKind.Absolute));
    }
    private static string? ResolvePath(Uri uri)
    {
        try
        {
            var path = uri.LocalPath;
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                path = path.Replace('/', '\\');
            
            return !Path.Exists(path) ? null : uri.LocalPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return null;
        }
    }
}