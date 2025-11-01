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
    HashFailed,
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

public abstract class QuicPeer
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

    private TaskCompletionSource<string>? fileHashReady;
    private bool controlReady;
    private bool fileReady;
    private Stopwatch? progressStopwatch;

    public bool IsTransferInProgress { get; private set; }

    protected static readonly TimeSpan connectionTimeout = TimeSpan.FromSeconds(30); 
    private static readonly TimeSpan timeoutCheckInterval = TimeSpan.FromSeconds(2);
    protected static readonly TimeSpan keepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly int fileChunkSize = 1024 * 1024;
    private static readonly int fileBufferSize = 16 * 1014 * 1024;
    private static readonly TimeSpan progressReportInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan speedEstimationInterval = TimeSpan.FromSeconds(2);
    
    public event Action<string>? OnDisconnected;
    public event Action<string, long>? OnFileOffered;
    public event Action? OnTransferStateChanged;
    public IProgress<ProgressInfo>? FileTransferProgress { get; set; }
    public TaskCompletionSource<(bool, string?)> FileOfferDecisionTsc { get; private set; } = new();
    
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
        fileHashReady = null;
        filePath = null;
        saveFolder = null;
        IsTransferInProgress = false;
        OnTransferStateChanged?.Invoke();
        progressStopwatch = null;
        lastSpeedUpdate = TimeSpan.Zero;
        previousBytes = 0;
        estimatedSpeed = 0;
        lastSpeedEstimation = TimeSpan.Zero;
    }
    
    private async Task 
        HandleControlMessage(string? line)
    {
        // Console.WriteLine(line);
        switch (line)
        {
            case null:
                break;
            case "READY": // Receiver gets this
                // Console.WriteLine("Receiver is ready, starting file send...");
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
                    case "FAILED":
                        // Console.WriteLine("Receiver did not receive the file successfully (integrity check failed).");
                        FileTransferCompleted?.SetResult(FileTransferStatus.HashFailed);
                        break;
                    default:
                        // Console.WriteLine($"Unknown status: {status}");
                        break;
                }
                ResetAfterFileTransferCompleted();
                break;
            
            case var _ when line.StartsWith("METADATA:"):   // Receiver gets this, marks the start of file transfer
                if (IsTransferInProgress)
                {
                    await QueueControlMessage("REJECTED:ALREADY_RECEIVING");
                    return;
                }
                IsTransferInProgress = true;
                OnTransferStateChanged?.Invoke();

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
                OnFileOffered?.Invoke(metadata["FileName"], long.Parse(metadata["FileSize"]));
                var (accepted, path) = await FileOfferDecisionTsc.Task;

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
                fileHashReady = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                await QueueControlMessage("READY");
                _ = Task.Run(ReceiveFileAsync, token);
                
                break;
            case var _ when line.StartsWith("FILE_SENT:"): // Receiver gets this
                // Console.WriteLine("Sender confirmed file was sent.");
                if (fileHashReady == null)
                    throw new InvalidOperationException("File hash ready not initialized.");
                var hash = line["FILE_SENT:".Length..];
                // Console.WriteLine($"Received file hash: {hash}");
                fileHashReady.SetResult(hash);
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
            ["FileSize"] = fileSize.ToString()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(meta);
        await QueueControlMessage($"METADATA:{json}");
    }

    private async Task SendFileAsync()
    {
        if (filePath == null)
            throw new InvalidOperationException("File path not set.");
        if (fileStream == null)
            throw new InvalidOperationException("File stream not initialized.");
        if (IsTransferInProgress)
        {
            return;
        }
        
        IsTransferInProgress = true;
        OnTransferStateChanged?.Invoke();

        var hashQueue = Channel.CreateBounded<ArraySegment<byte>>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        var hashTask = Task.Factory.StartNew(() => ComputeHashAsync(hashQueue), TaskCreationOptions.LongRunning)
            .Unwrap();

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
            await hashQueue.Writer.WriteAsync(new ArraySegment<byte>(buffer, 0, bytesRead), token);

            totalBytesSent += bytesRead;
            UpdateProgress(totalBytesSent, fileSize);
            await Task.Yield();
        }

        await fileStream.FlushAsync(token);
        progressStopwatch.Stop();
        hashQueue.Writer.Complete();
        
        var fileHash = await hashTask;
        ReportFinalProgress(totalBytesSent, fileSize);
        // Console.WriteLine(fileHash);
        await QueueControlMessage($"FILE_SENT:{fileHash}");
    }

    private async Task ReceiveFileAsync()
    {
        if (metadata == null)
            throw new Exception("The receiver was started prematurely.");

        if (fileStream == null)
            throw new InvalidOperationException("File stream not initialized.");

        if (JoinedFilePath == null)
            throw new InvalidOperationException("Joined file path not initialized.");

        long totalBytesReceived = 0;
        var fileSize = long.Parse(metadata["FileSize"]);

        var hashQueue = Channel.CreateBounded<ArraySegment<byte>>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        var hashTask = Task.Factory.StartNew(() => ComputeHashAsync(hashQueue), TaskCreationOptions.LongRunning)
            .Unwrap();

        await using var outputFile = new FileStream(
            JoinedFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: fileChunkSize,
            useAsync: true);

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
            await hashQueue.Writer.WriteAsync(new ArraySegment<byte>(buffer, 0, bytesRead), token);
            totalBytesReceived += bytesRead;
            UpdateProgress(totalBytesReceived, fileSize);
            await Task.Yield();
        }
        progressStopwatch.Stop();
        hashQueue.Writer.Complete();
        
        var actualFileHash = await hashTask;
        // Console.WriteLine($"SHA256: {actualFileHash}");
        await outputFile.FlushAsync(token);
        ReportFinalProgress(totalBytesReceived, fileSize);

        var expectedFileHash = await fileHashReady!.Task;
        var success = actualFileHash == expectedFileHash;
        
        if (!success)
        {
            // Console.WriteLine("[ERROR] File integrity check failed.");
            await QueueControlMessage("RECEIVED_FILE:FAILED");
            FileTransferCompleted!.SetResult(FileTransferStatus.HashFailed);
        }
        else
        {
            // Console.WriteLine("[SUCCESS] File received successfully.");
            await QueueControlMessage("RECEIVED_FILE:OK");
            FileTransferCompleted!.SetResult(FileTransferStatus.Completed);
        }
        
        ResetAfterFileTransferCompleted();
    }


    private async Task QueueControlMessage(string msg)
    {
        await controlSendQueue.Writer.WriteAsync(msg, token);
    }

    private async Task<string> ComputeHashAsync(Channel<ArraySegment<byte>> hashQueue)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await foreach (var segment in hashQueue.Reader.ReadAllAsync(token))
        {
            hasher.AppendData(segment.AsSpan());
            ArrayPool<byte>.Shared.Return(segment.Array!);
        }

        var hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public abstract Task StopAsync();
    
    protected async Task TimeoutCheckLoopAsync()
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(timeoutCheckInterval, token);
            try
            {
                await controlStream!.WriteAsync(Array.Empty<byte>(), token);
            }
            catch (QuicException ex) when (
                ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) 
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
        using var rsa = new RSACryptoServiceProvider(2048);
        var request = new CertificateRequest(
            "",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var notBefore = DateTime.UtcNow;
        var notAfter = notBefore.AddYears(100);
        var cert = request.CreateSelfSigned(notBefore, notAfter);

        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
}
