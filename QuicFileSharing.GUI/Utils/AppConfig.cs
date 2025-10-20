using System;
using System.IO;
using System.Text.Json;

namespace QuicFileSharing.GUI.Utils;

public class AppConfig
{
    public int PortV4 { get; set; } = 55441;
    public bool ForceIPv4 { get; set; } = false;
    public string SignalingServer { get; set; } = "ws://quic-share.zemendaniel.hu:8080";
    public string ApiV6 { get; set; } = "https://ipv6.seeip.org";
    public string ApiV4 { get; set; } = "https://api.ipify.org";
}

public static class DataStore
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicShare", "config.json");

    public static void Save(AppConfig data)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(data);

        var tempFile = Path.Combine(directory!, Path.GetRandomFileName());
        
        using (var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(json);
        }

        File.Move(tempFile, FilePath, overwrite: true);
    }


    public static AppConfig Load()
    {
        if (!File.Exists(FilePath))
            return new AppConfig(); 
        
        try
        {
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            return data;
        }
        catch (JsonException)
        {
            // Corrupted JSON
            return new AppConfig();
        }
        catch (IOException)
        {
            // I/O error
            return new AppConfig();
        }
    }

}