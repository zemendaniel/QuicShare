using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net.Quic;
using System.Text;
using System.Diagnostics;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Channels;

namespace QuicFileSharing.Core;


public enum FileTransferStatus
{
    Cancelled,
    Completed,
    RejectedAlreadySending,
    RejectedAlreadyReceiving,
    RejectedUnwanted,
    Etc
}

public class ProgressInfo
{
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage => TotalBytes == 0 ? 0 : (double)BytesTransferred / TotalBytes * 100;
    public double SpeedBytesPerSecond { get; set; } 
    public TimeSpan? EstimatedRemaining => SpeedBytesPerSecond > 0
        ? TimeSpan.FromSeconds((TotalBytes - BytesTransferred) / SpeedBytesPerSecond)
        : null;
    public bool IsCompleted { get; set; }
    public double? AverageSpeedBytesPerSecond { get; set; }
    public TimeSpan? TotalTime { get; set; }
}

public abstract class QuicPeer : IDisposable
{
    protected readonly X509Certificate2 cert = CreateSelfSignedCertificate();
    public string Thumbprint => cert.Thumbprint;

    protected QuicConnection? connection;
    protected QuicStream? controlStream;
    protected QuicStream? fileStream;
    protected CancellationTokenSource? cts;
    public TaskCompletionSource<bool> GotConnectedToPeer { get; } = new();

    public bool IsSending { get; set; }
    protected CancellationToken token = CancellationToken.None;
    private string? saveFolder; // receiver
    private string? filePath; // sender
    private Dictionary<string, string>? metadata; // receiver

    public string? JoinedFilePath { get; private set; }

    private readonly Channel<string> controlSendQueue = Channel.CreateUnbounded<string>();

    protected readonly TaskCompletionSource bothStreamsReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<FileTransferStatus>? FileTransferCompleted { get; private set; }

    private bool controlReady;
    private bool fileReady;
    private Stopwatch? progressStopwatch;

    public bool IsTransferInitiated { get; private set; }
    public bool IsTransferInProgress {get; private set;}

    protected static readonly TimeSpan connectionTimeout = TimeSpan.FromSeconds(30); 
    private static readonly TimeSpan timeoutCheckInterval = TimeSpan.FromSeconds(2);
    protected static readonly TimeSpan keepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly int fileChunkSize = 1024 * 1024;
    private static readonly int fileBufferSize = 16 * 1024 * 1024;
    private static readonly TimeSpan progressReportInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan speedEstimationInterval = TimeSpan.FromSeconds(2);
    
    public event Action<string>? OnDisconnected;
    public event Action<string, long, string>? OnFileOffered; // Updated signature
    public event Action? OnTransferInitiationStateChanged;
    public event Action? OnTransferStateChanged;
    public IProgress<ProgressInfo>? FileTransferProgress { get; set; }
    // Updated Tuple: (accepted, savePath, resumeOffset)
    public TaskCompletionSource<(bool accepted, string? savePath, long resumeOffset)> FileOfferDecisionTsc { get; private set; } = new();
    
    public void SetSendPath(string path)
    {
        filePath = path;
    }

    protected void SetControlStream()
    {
        controlReady = true;
        CompleteIfBothStreamsReady();
    }

    protected void SetFileStream()
    {
        fileReady = true;
        CompleteIfBothStreamsReady();
    }

    private Task WaitForStreamsAsync() => bothStreamsReady.Task;

    private void CompleteIfBothStreamsReady()
    {
        if (controlReady && fileReady)
        {
            bothStreamsReady.TrySetResult();
        }
    }
    
    protected void CallOnDisconnected(string reason) => OnDisconnected?.Invoke(reason);

    protected async Task ControlLoopAsync()
    {
        if (controlStream == null)
            throw new InvalidOperationException("Control stream not initialized.");

        var sendTask = Task.Run(async () =>
        {
            await foreach (var msg in controlSendQueue.Reader.ReadAllAsync(token))
            {
                var payload = Encoding.UTF8.GetBytes(msg);
                await SendMessageAsync(payload);
            }
        }, token);

        var receiveTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var payload = await ReadMessageAsync();
                if (payload == null) break; // stream closed
                var message = Encoding.UTF8.GetString(payload);
                _ = Task.Run(() => HandleControlMessage(message), token);
            }
        }, token);

        await Task.WhenAll(sendTask, receiveTask);
    }

    private async Task SendMessageAsync(ReadOnlyMemory<byte> payload)
    {
        if (controlStream == null) throw new InvalidOperationException("Control stream not initialized.");

        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length);

        await controlStream.WriteAsync(lenBuf, token);
        if (!payload.IsEmpty)
            await controlStream.WriteAsync(payload, token);
        await controlStream.FlushAsync(token);
    }

    private async Task<byte[]?> ReadMessageAsync()
    {
        if (controlStream == null) throw new InvalidOperationException("Control stream not initialized.");

        var lenBuf = new byte[4];
        try
        {
            await controlStream.ReadExactlyAsync(lenBuf, token);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (size < 0) throw new IOException("Invalid message size.");

        var payload = size == 0 ? Array.Empty<byte>() : new byte[size];
        if (size > 0)
        {
            await controlStream.ReadExactlyAsync(payload, token);
        }

        return payload;
    }

    private void ResetAfterFileTransferCompleted()
    {
        // Console.WriteLine("Resetting after file transfer completed.");
        IsSending = false;
        metadata = null;
        JoinedFilePath = null;
        filePath = null;
        saveFolder = null;
        IsTransferInitiated = false;
        OnTransferInitiationStateChanged?.Invoke();
        IsTransferInProgress = false;
        OnTransferStateChanged?.Invoke();
        progressStopwatch = null;
        lastSpeedUpdate = TimeSpan.Zero;
        previousBytes = 0;
        estimatedSpeed = 0;
        lastSpeedEstimation = TimeSpan.Zero;
    }
    
    private long _senderResumeOffset = 0;

    private async Task HandleControlMessage(string? line)
    {
        // Console.WriteLine(line);
        switch (line)
        {
            case null:
                break;
            case var _ when line.StartsWith("READY"): // Receiver gets this
                // READY or READY:offset
                long offset = 0;
                if (line.Contains(':'))
                {
                     var parts = line.Split(':');
                     if (parts.Length > 1 && long.TryParse(parts[1], out var parsed))
                     {
                         offset = parsed;
                     }
                }
                _senderResumeOffset = offset;
                // Console.WriteLine($"Receiver is ready (resume: {offset}), starting file send...");
                _ = Task.Run(SendFileAsync, token);
                break;
            case var _ when line.StartsWith("RECEIVED_FILE:"): // Sender gets this, marks the end of file transfer
                var status = line["RECEIVED_FILE:".Length..];
                switch (status)
                {
                    case "OK":
                        // Console.WriteLine("Receiver confirmed file was received successfully.");
                        FileTransferCompleted?.SetResult(FileTransferStatus.Completed);
                        break;
                    default:
                        // Console.WriteLine($"Unknown status: {status}");
                        break;
                }
                ResetAfterFileTransferCompleted();
                break;
            
            case var _ when line.StartsWith("METADATA:"):   // Receiver gets this, marks the start of file transfer
                if (IsTransferInitiated)
                {
                    await QueueControlMessage("REJECTED:ALREADY_RECEIVING");
                    return;
                }
                IsTransferInitiated = true;
                OnTransferInitiationStateChanged?.Invoke();

                if (IsSending)
                {
                    await QueueControlMessage("REJECTED:ALREADY_SENDING");
                    return;
                }
                
                FileTransferCompleted = new();
                
                var json = line["METADATA:".Length..];
                metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
                // Console.WriteLine($"Received metadata: {string.Join(", ", metadata)}");
                
                FileOfferDecisionTsc = new(TaskCreationOptions.RunContinuationsAsynchronously);
                
                // Extract FileId if available, else empty string
                metadata.TryGetValue("FileId", out var fileId);
                
                OnFileOffered?.Invoke(metadata["FileName"], long.Parse(metadata["FileSize"]), fileId ?? "");
                var (accepted, path, resumeOffset) = await FileOfferDecisionTsc.Task;

                if (!accepted)
                {
                    await QueueControlMessage("REJECTED:UNWANTED");
                    FileTransferCompleted?.SetResult(FileTransferStatus.Etc);
                    ResetAfterFileTransferCompleted();
                    return;
                }
                saveFolder = path;
               
                if (saveFolder == null)
                    throw new InvalidOperationException("Save folder not initialized.");
                JoinedFilePath = Path.Combine(saveFolder, metadata["FileName"]);
                
                if (resumeOffset > 0)
                    await QueueControlMessage($"READY:{resumeOffset}");
                else
                    await QueueControlMessage("READY");
                
                _ = Task.Run(() => ReceiveFileAsync(resumeOffset), token);
                
                break;
            case var _ when line.StartsWith("REJECTED:"): 
                IsSending = false;
                var reason = line["REJECTED:".Length..];
                switch (reason)
                {
                    case "ALREADY_SENDING":
                        FileTransferCompleted?.SetResult(FileTransferStatus.RejectedAlreadySending);
                        break;
                    case "UNWANTED":
                        FileTransferCompleted?.SetResult(FileTransferStatus.RejectedUnwanted);
                        break;
                    case "ALREADY_RECEIVING":
                        FileTransferCompleted?.SetResult(FileTransferStatus.RejectedAlreadyReceiving);
                        break;
                }
                ResetAfterFileTransferCompleted();
                break;
            default:
                // Console.WriteLine($"Unknown control message: {line}");
                break;
        }
    }

    public async Task StartSending()
    {
        IsTransferInitiated = true;
        OnTransferInitiationStateChanged?.Invoke();
        
        FileTransferCompleted = new();
        await WaitForStreamsAsync();
        if (filePath == null)
            throw new InvalidOperationException("File path not set.");
        if (!IsSending)
            throw new InvalidOperationException("Not in sending mode.");

        var fileInfo = new FileInfo(filePath);
        var fileName = Path.GetFileName(filePath);
        var fileSize = fileInfo.Length;
        var meta = new Dictionary<string, string>
        {
            ["FileName"] = fileName,
            ["FileSize"] = fileSize.ToString(),
            ["FileId"] = GenerateFileId(fileInfo)
        };
        var json = System.Text.Json.JsonSerializer.Serialize(meta);
        await QueueControlMessage($"METADATA:{json}");
    }

    public static string GenerateFileId(FileInfo info)
    {
        var raw = $"{info.Name}|{info.Length}|{info.CreationTimeUtc:O}|{info.LastWriteTimeUtc:O}";
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..16];
    }

    private async Task SendFileAsync()
    {
        if (IsTransferInProgress)
        {
            return;
        }
        IsTransferInProgress = true;
        OnTransferStateChanged?.Invoke();
        
        if (filePath == null)
            throw new InvalidOperationException("File path not set.");
        if (fileStream == null)
            throw new InvalidOperationException("File stream not initialized.");
        
        await using var inputFile = new FileStream(
            path: filePath,
            mode: FileMode.Open,
            access: FileAccess.Read,
            share: FileShare.Read,
            bufferSize: fileBufferSize,
            useAsync: true
        );
        
        long totalBytesSent = 0;
        var fileSize = inputFile.Length;
        progressStopwatch = Stopwatch.StartNew();

        // Resume Logic: Skip bytes
        if (_senderResumeOffset > 0)
        {
             // Simply seek the input file! No need to read and hash.
             if (inputFile.CanSeek)
             {
                 inputFile.Seek(_senderResumeOffset, SeekOrigin.Begin);
                 totalBytesSent = _senderResumeOffset;
             }
             else
             {
                // Fallback if not seekable (unlikely for FileStream)
                long skipped = 0;
                var skipBuffer = ArrayPool<byte>.Shared.Rent(fileChunkSize);
                try 
                {
                    while (skipped < _senderResumeOffset)
                    {
                        var toRead = (int)Math.Min(fileChunkSize, _senderResumeOffset - skipped);
                        var bytesRead = await inputFile.ReadAsync(skipBuffer.AsMemory(0, toRead), token);
                        if (bytesRead == 0) break;
                        skipped += bytesRead;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(skipBuffer);
                }
                totalBytesSent = skipped;
             }
        }
        
        while (true)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(fileChunkSize);
            var bytesRead = await inputFile.ReadAsync(buffer.AsMemory(0, fileChunkSize), token);
            if (bytesRead == 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);

            totalBytesSent += bytesRead;
            UpdateProgress(totalBytesSent, fileSize);
            await Task.Yield();
        }

        await fileStream.FlushAsync(token);
        progressStopwatch.Stop();
        
        ReportFinalProgress(totalBytesSent, fileSize);
    }

    private async Task ReceiveFileAsync(long resumeOffset = 0)
    {
        if (IsTransferInProgress)
        {
            return;
        }
        IsTransferInProgress = true;
        OnTransferStateChanged?.Invoke();
        
        if (metadata == null)
            throw new Exception("The receiver was started prematurely.");

        if (fileStream == null)
            throw new InvalidOperationException("File stream not initialized.");

        if (JoinedFilePath == null)
            throw new InvalidOperationException("Joined file path not initialized.");

        // The partial file path is what we are writing to
        var partFilePath = JoinedFilePath + ".qs_part";
        
        // Open/Create for appending/writing
        // If resuming, Open and Seek to End (which should match resumeOffset)
        // If not resuming, Create (overwrite)
        FileMode mode = (resumeOffset > 0) ? FileMode.Open : FileMode.Create;
        
        await using var outputFile = new FileStream(
            partFilePath,
            mode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: fileChunkSize,
            useAsync: true);

        if (resumeOffset > 0)
        {
             outputFile.Seek(0, SeekOrigin.End);
        }

        long totalBytesReceived = resumeOffset;
        var fileSize = long.Parse(metadata["FileSize"]);

        progressStopwatch = Stopwatch.StartNew();

        while (totalBytesReceived < fileSize)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(fileChunkSize);
            var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, fileChunkSize), token);
            if (bytesRead == 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                break;
            }
            await outputFile.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            totalBytesReceived += bytesRead;
            UpdateProgress(totalBytesReceived, fileSize);
            await Task.Yield();
        }
        progressStopwatch.Stop();
        
        // No heavy Flushing in loop requested, but we flush at end
        await outputFile.FlushAsync(token);
        
        // Wait for file handle to close before move
        await outputFile.DisposeAsync();

        ReportFinalProgress(totalBytesReceived, fileSize);

        // Console.WriteLine("[SUCCESS] File received successfully.");
        // Rename .qs_part to final
        
        // Note: JoinedFilePath is the final path
        if (File.Exists(JoinedFilePath)) File.Delete(JoinedFilePath);
        File.Move(partFilePath, JoinedFilePath);
        
        await QueueControlMessage("RECEIVED_FILE:OK");
        FileTransferCompleted!.SetResult(FileTransferStatus.Completed);
        
        ResetAfterFileTransferCompleted();
    }


    private async Task QueueControlMessage(string msg)
    {
        await controlSendQueue.Writer.WriteAsync(msg, token);
    }


    public abstract Task StopAsync();

    public virtual void Dispose()
    {
        cert.Dispose();
        cts?.Dispose();
        if (controlStream != null) _ = controlStream.DisposeAsync();
        if (fileStream != null) _ = fileStream.DisposeAsync();
        if (connection != null) _ = connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task TimeoutCheckLoopAsync()
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(timeoutCheckInterval, token);
            try
            {
                await controlStream!.WriteAsync(Array.Empty<byte>(), token);
            }
            catch (QuicException)
            {
                CallOnDisconnected("You got disconnected from your peer.");
                await StopAsync();
                return;
            }
        }
    }
    
    private TimeSpan lastSpeedUpdate = TimeSpan.Zero;
    private long previousBytes;
    private TimeSpan lastSpeedEstimation = TimeSpan.Zero;
    private double estimatedSpeed;


    private void UpdateProgress(long bytesTransferred, long totalBytes)
    {
        if (progressStopwatch == null)
            return;

        var elapsedSinceLastReport = progressStopwatch.Elapsed - lastSpeedUpdate;

        // Report UI progress every 0.5 s
        if (elapsedSinceLastReport >= progressReportInterval)
        {
            lastSpeedUpdate = progressStopwatch.Elapsed;

            // Update the estimated speed only every 2 seconds
            var elapsedSinceLastEstimation = progressStopwatch.Elapsed - lastSpeedEstimation;
            if (elapsedSinceLastEstimation >= speedEstimationInterval)
            {
                estimatedSpeed = (bytesTransferred - previousBytes) / elapsedSinceLastEstimation.TotalSeconds;
                previousBytes = bytesTransferred;
                lastSpeedEstimation = progressStopwatch.Elapsed;
            }

            FileTransferProgress?.Report(new ProgressInfo
            {
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                SpeedBytesPerSecond = estimatedSpeed
            });
        }
    }



    private void ReportFinalProgress(long totalBytesSent, long fileSize)
    {
        if (progressStopwatch == null)
            return;
        
        FileTransferProgress?.Report(new ProgressInfo
        {
            BytesTransferred = totalBytesSent,
            TotalBytes = fileSize,
            IsCompleted = true,
            AverageSpeedBytesPerSecond = totalBytesSent / progressStopwatch.Elapsed.TotalSeconds,
            TotalTime = progressStopwatch.Elapsed
        });
    }
    

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var ecdsa = ECDsa.Create();
        var request = new CertificateRequest("CN=quicshare-peer", ecdsa, HashAlgorithmName.SHA256);

        // Standard usages for TLS
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { 
                new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
            }, false));

        var notBefore = DateTime.UtcNow.AddDays(-1); 
        var notAfter = notBefore.AddYears(10); 

        // Generate the cert
        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        // Export to PFX bytes
        // Export to PFX bytes
        byte[] pfxBytes = cert.Export(X509ContentType.Pfx, "dummy-password");

        // FIX: Use PersistKeySet so Win10/macOS creates a formal key container.
        // Exportable ensures MsQuic/OpenSSL is allowed to read it for the handshake.
        return X509CertificateLoader.LoadPkcs12(
            pfxBytes, 
            "dummy-password", 
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }
}
