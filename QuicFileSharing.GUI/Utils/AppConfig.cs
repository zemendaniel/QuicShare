using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicFileSharing.GUI.Utils;

public class AppConfig
{
    public int PortV4 { get; set; }
    public bool UseFixedPort { get; set; }
    public string SignalingServer { get; set; }
    public string StunServer { get; set; }
    public string SenderPath { get; set; }
    public string ReceiverPath { get; set; }

    private const int DefaultPort = 39805;
    private const bool DefaultUseFixedPort = false;
    private const string DefaultSignalingServer = "wss://quic-share.zemendaniel.hu";
    private const string DefaultStunServer = "stun.l.google.com:19302";
    private const string DefaultSenderPath = "";
    private static readonly string DefaultReceiverPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    public AppConfig(
        int? portV4 = null,
        bool? useFixedPort = null,
        string? signalingServer = null,
        string? stunServer = null,
        string? senderPath = null,
        string? receiverPath = null
        )
    {
        PortV4 = portV4 ?? DefaultPort;
        UseFixedPort = useFixedPort ?? DefaultUseFixedPort;
        SignalingServer = string.IsNullOrWhiteSpace(signalingServer) ? DefaultSignalingServer : signalingServer;
        StunServer = string.IsNullOrWhiteSpace(stunServer) ? DefaultStunServer : stunServer;
        SenderPath = string.IsNullOrWhiteSpace(senderPath) ? DefaultSenderPath : senderPath;
        ReceiverPath = string.IsNullOrWhiteSpace(receiverPath) ? DefaultReceiverPath : receiverPath;
    }
    
    [JsonConstructor]
    public AppConfig(int PortV4, bool UseFixedPort, string SignalingServer, string StunServer, string SenderPath, string ReceiverPath)
    {
        this.PortV4 = PortV4;
        this.UseFixedPort = UseFixedPort;
        this.SignalingServer = SignalingServer;
        this.StunServer = StunServer;
        this.SenderPath = SenderPath;
        this.ReceiverPath = ReceiverPath;
    }
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