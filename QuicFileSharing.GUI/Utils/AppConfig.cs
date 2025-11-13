using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicFileSharing.GUI.Utils;

public class AppConfig
{
    public int PortV4 { get; set; }
    public bool ForceIPv4 { get; set; }
    public string SignalingServer { get; set; }
    public string ApiV6 { get; set; }
    public string ApiV4 { get; set; }
    public string SenderPath { get; set; }
    public string ReceiverPath { get; set; }

    private const int DefaultPortV4 = 55441;
    private const bool DefaultForceIPv4 = false;
    private const string DefaultSignalingServer = "wss://quic-share.zemendaniel.hu";
    private const string DefaultApiV6 = "https://ipv6.seeip.org";
    private const string DefaultApiV4 = "https://api.ipify.org";
    private const string DefaultSenderPath = "";
    private static readonly string DefaultReceiverPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    public AppConfig(
        int? portV4 = null,
        bool? forceIPv4 = null,
        string? signalingServer = null,
        string? apiV6 = null,
        string? apiV4 = null,
        string? senderPath = null,
        string? receiverPath = null
        )
    {
        PortV4 = portV4 ?? DefaultPortV4;
        ForceIPv4 = forceIPv4 ?? DefaultForceIPv4;
        SignalingServer = string.IsNullOrWhiteSpace(signalingServer) ? DefaultSignalingServer : signalingServer;
        ApiV6 = string.IsNullOrWhiteSpace(apiV6) ? DefaultApiV6 : apiV6;
        ApiV4 = string.IsNullOrWhiteSpace(apiV4) ? DefaultApiV4 : apiV4;
        SenderPath = string.IsNullOrWhiteSpace(senderPath) ? DefaultSenderPath : senderPath;
        ReceiverPath = string.IsNullOrWhiteSpace(receiverPath) ? DefaultReceiverPath : receiverPath;
    }
    
    [JsonConstructor]
    public AppConfig(int PortV4, bool ForceIPv4, string SignalingServer, string ApiV6, string ApiV4, string SenderPath, string ReceiverPath)
    {
        this.PortV4 = PortV4;
        this.ForceIPv4 = ForceIPv4;
        this.SignalingServer = SignalingServer;
        this.ApiV6 = ApiV6;
        this.ApiV4 = ApiV4;
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