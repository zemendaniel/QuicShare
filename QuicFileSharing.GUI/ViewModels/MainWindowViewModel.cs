using System;
using System.IO;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using QuicFileSharing.Core;
using QuicFileSharing.GUI.Models;
using QuicFileSharing.GUI.Utils;
using QuicFileSharing.GUI.Views;


namespace QuicFileSharing.GUI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private AppState state = AppState.Lobby;
    [ObservableProperty]
    private string roomCode = string.Empty;
    [ObservableProperty]
    private string roomLoadingMessage = string.Empty;
    [ObservableProperty]
    private string statusMessage = string.Empty;
    [ObservableProperty]
    private string lobbyText = string.Empty;
    [ObservableProperty]
    private string roomText = string.Empty;
    [ObservableProperty]
    private double progressPercentage; 
    [ObservableProperty]
    private string progressText = string.Empty;
    [ObservableProperty]
    private bool forceIPv4;
    [ObservableProperty]
    private string portV4Text = string.Empty;
    [ObservableProperty] 
    private string apiV4Text = string.Empty;
    [ObservableProperty] 
    private string apiV6Text = string.Empty;
    [ObservableProperty]
    private string signalingServerText = string.Empty;
    [ObservableProperty]
    private string settingsText = string.Empty;
    [ObservableProperty]
    private bool isTransferInProgress;
    [ObservableProperty]
    private string filePath = string.Empty;    
    
    private readonly AppConfig appConfig = DataStore.Load();
    private CancellationTokenSource cts;
    private QuicPeer peer;
    
    
    public MainWindowViewModel()
    {
        LoadConfig();
    }

    public async Task CheckQuicSupportAsync(Window window)
    {
        // if (!QuicListener.IsSupported || !QuicConnection.IsSupported) 
        if (false) // todo remove 
        {
            var msgBox = MessageBoxManager
                .GetMessageBoxStandard(
                    "Unsupported Feature",
                    "QUIC is not supported on this system.\nThe application will now close.",
                    ButtonEnum.Ok,
                    Icon.Error);

            await msgBox.ShowAsPopupAsync(window);

            window.Close();
        }
    }

    private void LoadConfig()
    {
        ForceIPv4 = appConfig.ForceIPv4;
        PortV4Text = appConfig.PortV4.ToString();
        ApiV4Text = appConfig.ApiV4;
        ApiV6Text = appConfig.ApiV6;
        SignalingServerText = appConfig.SignalingServer;
    }
    
    [RelayCommand]
    private async Task JoinRoom()
    {
        if (string.IsNullOrWhiteSpace(RoomCode))
        {
            LobbyText = "Please enter a room code.";
            return;
        }
        peer = new Client(); 
        SetPeerHandlers();
        var client = (peer as Client)!;
        
        using var signalingUtils = new SignalingUtils(appConfig.ApiV4, appConfig.ApiV6);
        await using var signaling = new WebSocketSignaling(appConfig.SignalingServer);
        
        cts = new CancellationTokenSource();

        signaling.OnDisconnected += async (_, description) =>
        {
            if (client.GotConnectedToPeer.Task.IsCompleted) return;
            if (cts.Token.IsCancellationRequested) return;
            await cts.CancelAsync();
            RoomCode = string.Empty;
            LobbyText = $"Disconnected from coordination server: {(string.IsNullOrEmpty(description) ? "Something went wrong with your peer." : description)}";
            State = AppState.Lobby;

        };
        LobbyText = "Connecting to peer...";
        try
        {
            var (success, errorMessage) =
                await Task.Run(() => signaling.ConnectAsync(Role.Client, RoomCode.Trim().ToUpper()), cts.Token);
            if (success is not true)
            {
                State = AppState.Lobby;
                LobbyText = $"Could not connect to coordination server: {errorMessage}";
                return;
            }

            var offer = await Task.Run(() => signalingUtils.ConstructOfferAsync(client.Thumbprint), cts.Token);

            try
            {
                await Task.Run(() => signaling.SendAsync(offer, "offer"), cts.Token);
            }
            catch (InvalidOperationException ex)
            {
                await cts.CancelAsync();
                LobbyText = $"Could not connect to coordination server: {ex.Message}";
            }

            var answer = await signaling.AnswerTsc.Task.WaitAsync(cts.Token);
            try
            {
                signalingUtils.ProcessAnswer(answer);
            }
            catch (Exception ex)
            {
                await cts.CancelAsync();
                LobbyText = $"Could not connect to peer: {ex.Message}";
                return;           
            }
            if (signalingUtils.PeerIp == null)
            {
                await cts.CancelAsync();
                LobbyText = "Could not connect to peer: Could not agree on IP generation.";
                return;
            }

            try
            {
                signalingUtils.CloseUdpSocket();
                await Task.Run(() => client.StartAsync(
                    signalingUtils.PeerIp,
                    signalingUtils.PeerPort,
                    signalingUtils.PeerIp.AddressFamily == AddressFamily.InterNetworkV6,
                    signalingUtils.ServerThumbprint!,
                    signalingUtils.OwnPort), cts.Token);
            }
            catch (QuicException ex)
            {
                await cts.CancelAsync();
                LobbyText =
                    $"Could not connect to peer: {ex.Message}. " +
                    $"Make sure that you have a working internet connection and firewall is not blocking QUIC traffic.";
                return;
            }
            catch (Exception ex)
            {

                await cts.CancelAsync();
                LobbyText = "Could not connect to peer: " + ex.Message;
                return;
            }
            
            try
            {
                var isCertValid = await client.GotConnectedToPeer.Task.WaitAsync(cts.Token);
                if (!isCertValid)
                    return;
            }
            catch (TaskCanceledException)
            {
                // ignored
            }

            ProgressPercentage = 0;
            ProgressText = "";
            State = AppState.InRoom;
            await Task.Run(signaling.CloseAsync, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    [RelayCommand]
    private async Task CreateRoom()
    {
        peer = new Server();
        SetPeerHandlers();

        LobbyText = "Connecting to coordination server...";
        var server = (peer as Server)!;
        cts = new CancellationTokenSource();
        using var signalingUtils = new SignalingUtils(appConfig.ApiV4, appConfig.ApiV6);
        await using var signaling = new WebSocketSignaling(appConfig.SignalingServer);
        
        signaling.OnDisconnected += async (_, description) =>
        {
            if (server.GotConnectedToPeer.Task.IsCompleted) return;
            if (cts.Token.IsCancellationRequested) return;
            await cts.CancelAsync();
            RoomCode = string.Empty;
            LobbyText = $"Disconnected from coordination server: {(string.IsNullOrEmpty(description) ?
                "The signaling was closed before your peer could join." : description)}";
            State = AppState.Lobby;            
        };
        var (success, errorMessage) = await Task.Run(() => signaling.ConnectAsync(Role.Server), cts.Token);
        if (success is not true)
        { 
            State = AppState.Lobby; 
            LobbyText = $"Could not connect to coordination server: {errorMessage}";
            return;
        }        
        var info = await signaling.RoomInfoTcs.Task.WaitAsync(cts.Token);
        
        State = AppState.WaitingForConnection;
        RoomCode = info.id;

        var offer = await signaling.OfferTcs.Task.WaitAsync(cts.Token);;
        string answer;
        try
        {
            answer = await Task.Run(() => 
                signalingUtils.ConstructAnswerAsync(offer, server.Thumbprint, ForceIPv4, appConfig.PortV4), cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            await cts.CancelAsync();
            State = AppState.Lobby;
            LobbyText = $"Failed to accept connection: {ex.Message}";
            return;       
        }

        signalingUtils.CloseUdpSocket();
        await Task.Run(() => server.StartAsync(!ForceIPv4, signalingUtils.OwnPort ?? appConfig.PortV4,
            signalingUtils.ClientThumbprint!), cts.Token);
        
        try
        {
            await Task.Run(() => signaling.SendAsync(answer, "answer"), cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            State = AppState.Lobby;
            LobbyText = $"Could not connect to coordination server: {ex.Message}";
        }

        try
        {
            var isCertValid = await server.GotConnectedToPeer.Task.WaitAsync(cts.Token);
            if (!isCertValid)
                return;
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
        
        ProgressPercentage = 0;
        ProgressText = "";
        State = AppState.InRoom;
        await Task.Run(signaling.CloseAsync, CancellationToken.None);
    }

    [RelayCommand]
    private async Task SendFile(Window window) 
    {
        peer.IsSending = true;
        RoomText = "";
        TrackProgress();
        
        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(appConfig.SenderPath) &&
            Directory.Exists(appConfig.SenderPath))
        {
            startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(appConfig.SenderPath);
        }
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file to send",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation 
        });

        if (files.Count == 0)
        {
            peer.IsSending = false;
            RoomText = "No file was selected.";
            return;
        }
        var file = files[0];
        var path = FileUtils.ResolveFilePath(file);
        if (path is null)
        {
            peer.IsSending = false;
            RoomText = "Error: Could not determine file path.";
            return;       
        }
        if (!FileUtils.CanReadFile(path))
        {
            peer.IsSending = false;
            RoomText = $"Error: Permission denied for file: {path}";
            return;      
        }
        var selectedDir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(selectedDir))
        {
            appConfig.SenderPath = selectedDir;
            DataStore.Save(appConfig);
        }
        peer.SetSendPath(path);
        FilePath = $"Selected file: {path}";
        await peer.StartSending();
        RoomText = "Waiting for peer to accept file...";
        var status = await peer.FileTransferCompleted!.Task;
        peer.IsSending = false;
        HandleFileTransferCompleted(status);
    }

    private void SetPeerHandlers()
    {
        peer.OnDisconnected += async msg =>
        {
            await cts.CancelAsync();
            LobbyText = $"Connection Error: {msg}";
            RoomCode = "";
            State = AppState.Lobby;
            IsTransferInProgress = false;
        };
        peer.OnFileOffered += async (fileName, fileSize) =>
        {
            (bool accepted, string? path) result = default;
            await Dispatcher.UIThread.InvokeAsync( async () =>
            {
                var dialog = new FileOfferDialog
                {
                    DataContext = new FileOfferDialogViewModel(fileName, fileSize, appConfig.ReceiverPath)
                };
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    result = await dialog.ShowDialog<(bool accepted, string? path)>(desktop.MainWindow!);
                    peer.FileOfferDecisionTsc.SetResult(result);
                    if (result.accepted)
                        TrackProgress();
                }
            });
            if (!result.accepted)
                return;
            appConfig.ReceiverPath = result.path ?? string.Empty;
            DataStore.Save(appConfig);
            FilePath = $"File is located at: {peer.JoinedFilePath ?? "Unknown file path"}";
            var status = await peer.FileTransferCompleted!.Task;
            HandleFileTransferCompleted(status);
        };
        peer.OnTransferStateChanged += () =>
        {
            IsTransferInProgress = peer.IsTransferInProgress;
            if (IsTransferInProgress)
                RoomText = "";
        };
    }

    private void HandleFileTransferCompleted(FileTransferStatus status)
    {
        switch (status)
        {
            case FileTransferStatus.HashFailed:
                RoomText = "Error: File transfer was not successful because the file got corrupted during transfer.";
                break;
            case FileTransferStatus.RejectedAlreadySending:
                RoomText = "File rejected: Your peer is already sending or preparing to send a file.";
                break;
            case FileTransferStatus.RejectedAlreadyReceiving:
                RoomText = "File rejected: Your peer is already receiving or preparing to receive a file.";
                break;
            case FileTransferStatus.RejectedUnwanted:
                RoomText = "File rejected: Your peer is not interested in receiving this file.";
                break;
            case FileTransferStatus.Cancelled:
                RoomText = "File transfer was cancelled.";
                break;
            case FileTransferStatus.Completed:
                RoomText = "File transfer completed successfully.";
                break;
        }
    }

    private void TrackProgress()
    {
        peer.FileTransferProgress = new Progress<ProgressInfo>(info =>
        {
            ProgressPercentage = info.Percentage;

            var sb = new StringBuilder();

            sb.Append($"{ProgressPercentage:F1}% — ");
            sb.Append($"{FormatBytes(info.BytesTransferred)} / {FormatBytes(info.TotalBytes)}");

            if (info.SpeedBytesPerSecond > 0)
                sb.Append($" ({FormatBytes((long)info.SpeedBytesPerSecond)}/s)");

            if (info.EstimatedRemaining is { } eta)
                sb.Append($" ({StaticUtils.FormatTime(eta)})");

            if (info.IsCompleted)
            {
                if (info is { AverageSpeedBytesPerSecond: not null, TotalTime: not null })
                {
                    sb.Clear();
                    sb.Append($"{FormatBytes(info.TotalBytes)} transferred in {StaticUtils.FormatTimeShort(info.TotalTime.Value)} ");
                    sb.Append($"({FormatBytes((long)info.AverageSpeedBytesPerSecond.Value)}/s average)");
                }
            }
            ProgressText = sb.ToString();
        });
    }
    
    [RelayCommand]
    private void BackToLobby()
    {
        State = AppState.Lobby;
    }
    [RelayCommand]
    private void OpenSettings()
    {
        State = AppState.Settings;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SettingsText = "";
        int port;
        if (!string.IsNullOrWhiteSpace(PortV4Text))
        {
            if (!int.TryParse(PortV4Text, out port) || port < 1 || port > 65535)
            {
                SettingsText = "Invalid port number. Must be between 1 and 65535.";
                return;
            }
        }
        else
        {
            port = new AppConfig().PortV4;
        }

        if (!string.IsNullOrWhiteSpace(SignalingServerText) &&
            !StaticUtils.IsValidWebSocketUri(SignalingServerText))
        {
            SettingsText = "Invalid signaling server URL. " +
                           "Make sure it starts with ws:// or wss:// and that the host and port are correct.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApiV6Text) && !StaticUtils.IsValidHttpUrl(ApiV6Text))
        {
            SettingsText = "Invalid API URL for IPv6. " +
                           "Make sure it starts with http:// or https:// and that the host, port and path are correct.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApiV4Text) && !StaticUtils.IsValidHttpUrl(ApiV4Text))
        {
            SettingsText = "Invalid API URL for IPv4. " +
                           "Make sure it starts with http:// or https:// and that the host, port and path are correct.";
            return;
        }

        var defaults = new AppConfig(); 

        var config = new AppConfig
        {
            PortV4 = port,
            ForceIPv4 = ForceIPv4,
            SignalingServer = string.IsNullOrWhiteSpace(SignalingServerText) 
                ? defaults.SignalingServer 
                : SignalingServerText,
            ApiV6 = string.IsNullOrWhiteSpace(ApiV6Text) 
                ? defaults.ApiV6 
                : ApiV6Text,
            ApiV4 = string.IsNullOrWhiteSpace(ApiV4Text) 
                ? defaults.ApiV4 
                : ApiV4Text
        };

        DataStore.Save(config);
        LoadConfig();
        State = AppState.Lobby;
    }
    
    [RelayCommand]
    private void CloseSettings()
    {
        LoadConfig();
        State = AppState.Lobby;
        SettingsText = "";
    }

    [RelayCommand]
    private async Task CopyRoomCode(Window window)
    {
        if (window.Clipboard is null)
            return;
        
        await window.Clipboard.SetTextAsync(RoomCode);
    }
}
