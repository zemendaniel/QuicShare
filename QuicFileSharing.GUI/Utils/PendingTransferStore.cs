using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace QuicFileSharing.GUI.Utils;

public class PendingTransfer
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string SaveFolder { get; set; } = string.Empty;
    // We derive the part path from SaveFolder + FileName + ".qs_part"
    public string PartFilePath => Path.Combine(SaveFolder, FileName + ".qs_part");
    public double Progress => File.Exists(PartFilePath) ? (double)new FileInfo(PartFilePath).Length / FileSize * 100 : 0;
}

public static class PendingTransferStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "QuicShare", 
        "pending");

    static PendingTransferStore()
    {
        if (!Directory.Exists(StorePath))
        {
            Directory.CreateDirectory(StorePath);
        }
    }

    public static void Save(PendingTransfer transfer)
    {
        var filePath = Path.Combine(StorePath, transfer.FileId + ".json");
        var json = JsonSerializer.Serialize(transfer);
        File.WriteAllText(filePath, json);
    }

    public static PendingTransfer? Load(string fileId)
    {
        var filePath = Path.Combine(StorePath, fileId + ".json");
        if (!File.Exists(filePath)) return null;
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<PendingTransfer>(json);
        }
        catch
        {
            return null;
        }
    }
    
    public static List<PendingTransfer> LoadAll()
    {
        var list = new List<PendingTransfer>();
        var files = Directory.GetFiles(StorePath, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var obj = JsonSerializer.Deserialize<PendingTransfer>(json);
                if (obj != null) list.Add(obj);
            }
            catch { /* ignore corrupted */ }
        }
        return list;
    }

    public static void Delete(string fileId)
    {
        var filePath = Path.Combine(StorePath, fileId + ".json");
        if (File.Exists(filePath)) File.Delete(filePath);
    }
    
    public static void DeleteWithFile(string fileId)
    {
        var transfer = Load(fileId);
        if (transfer != null)
        {
            if (File.Exists(transfer.PartFilePath))
            {
                try { File.Delete(transfer.PartFilePath); } catch {}
            }
        }
        Delete(fileId);
    }
}

