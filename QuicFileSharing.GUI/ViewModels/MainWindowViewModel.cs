using System;
using System.IO;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.CompilerServices;
using QuicFileSharing.Core;
using QuicFileSharing.GUI.Models;
using Avalonia.Styling;
using QuicFileSharing.GUI.Utils;
using QuicFileSharing.GUI.Views;


namespace QuicFileSharing.GUI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string WsBaseUri = "ws://152.53.123.174:8080";
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
    private string portV4Text;
    [ObservableProperty] 
    private string apiV4Text;
    [ObservableProperty] 
    private string apiV6Text;
    [ObservableProperty]
    private string signalingServerText;
    [ObservableProperty]
    private string settingsText = string.Empty;
    
    private AppConfig appConfig;
    
    private int PortV4 => appConfig.PortV4;

    // [ObservableProperty] 
    // private bool isTransferring => peer.IsTransferInProgress; 

    public MainWindowViewModel()
    {
        LoadConfig();
    }

    private void LoadConfig()
    {
        appConfig = DataStore.Load();
        ForceIPv4 = appConfig.ForceIPv4;
        PortV4Text = appConfig.PortV4.ToString();
        ApiV4Text = appConfig.ApiV4;
        ApiV6Text = appConfig.ApiV6;
        SignalingServerText = appConfig.SignalingServer;
    }
    
    private QuicPeer peer;
    
    [RelayCommand]
    private async Task JoinRoom()
    {
        peer = new Client(); 
        SetPeerHandlers();
        var client = (peer as Client)!;

        ProgressText = "";
        ProgressPercentage = 0;
        
        using var signalingUtils = new SignalingUtils(appConfig.ApiV4, appConfig.ApiV6);
        await using var signaling = new WebSocketSignaling(WsBaseUri);
        
        var cts = new CancellationTokenSource();

        signaling.OnDisconnected += async (_, description) =>
        {
            if (client.GotConnected) return;
            if (cts.Token.IsCancellationRequested) return;
            await cts.CancelAsync();
            State = AppState.Lobby;
            LobbyText = $"Disconnected from coordination server: {(string.IsNullOrEmpty(description) ? "Something went wrong with your peer." : description)}";
            RoomCode = "";

        };
        LobbyText = "Connecting to peer...";
        try
        {
            var (success, errorMessage) =
                await Task.Run(() => signaling.ConnectAsync(Role.Client, RoomCode.Trim().ToLower()), cts.Token);
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
        var cts = new CancellationTokenSource();
        using var signalingUtils = new SignalingUtils(appConfig.ApiV4, appConfig.ApiV6);
        await using var signaling = new WebSocketSignaling(WsBaseUri);
        
        signaling.OnDisconnected += async (_, description) =>
        {
            if (server.ClientConnected.Task.IsCompleted) return;
            if (cts.Token.IsCancellationRequested) return;
            await cts.CancelAsync();
            State = AppState.Lobby;            
            LobbyText = $"Disconnected from coordination server: {(string.IsNullOrEmpty(description) ?
                "The signaling was closed before your peer could join." : description)}";
            RoomCode = "";
        };
        var (success, errorMessage) = await Task.Run(() => signaling.ConnectAsync(Role.Server), cts.Token);
        if (success is not true)
        { 
            State = AppState.Lobby; 
            LobbyText = $"Could not connect to coordination server: {errorMessage}";
            return;
        }        
        var info = await signaling.RoomInfoTcs.Task;
        
        State = AppState.WaitingForConnection;
        RoomCode = info.id;

        var offer = await signaling.OfferTcs.Task;
        string answer;
        try
        {
            answer = await Task.Run(() => 
                signalingUtils.ConstructAnswerAsync(offer, server.Thumbprint, ForceIPv4, PortV4), cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            await cts.CancelAsync();
            State = AppState.Lobby;
            LobbyText = $"Failed to accept connection: {ex.Message}";
            return;       
        }

        signalingUtils.CloseUdpSocket();
        await Task.Run(() => server.StartAsync(!ForceIPv4, signalingUtils.OwnPort ?? PortV4,
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

        await server.ClientConnected.Task;
        State = AppState.InRoom;
        await Task.Run(signaling.CloseAsync, CancellationToken.None);
    }

    [RelayCommand]
    private async Task SendFile(Window window) 
    {
        peer.IsSending = true;
        RoomText = "";
        TrackProgress();
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file to send",
            AllowMultiple = false
        });

        if (files.Count == 0)
        {
            peer.IsSending = false;
            RoomText = "No file was selected.";
            return;
        }
        var file = files[0];
        var path = StaticUtils.ResolveFilePath(file);
        if (path is null)
        {
            peer.IsSending = false;
            RoomText = "Error: Could not determine file path.";
            return;       
        }
        peer.SetSendPath(path);
        await peer.StartSending();
        var status = await peer.FileTransferCompleted!.Task;
        peer.IsSending = false;
        HandleFileTransferCompleted(status);
    }

    private void SetPeerHandlers()
    {
        peer.OnDisconnected += () =>
        {
            LobbyText = "Connection Error: You got disconnected from your peer.";
            RoomCode = "";
            State = AppState.Lobby;
        };
        peer.OnFileOffered += async (fileName, fileSize) =>
        {
            await Dispatcher.UIThread.InvokeAsync( async () =>
            {
                var dialog = new FileOfferDialog
                {
                    DataContext = new FileOfferDialogViewModel(fileName, fileSize)
                };
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var result = await dialog.ShowDialog<(bool accepted, string? path)>(desktop.MainWindow!);
                    peer.FileOfferDecisionTsc.SetResult(result);
                    if (result.accepted)
                        TrackProgress();
                }
            });
        };
        peer.OnFileRejected += msg =>
        {
            RoomText = msg;
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
                           "Make sure it starts with ws:// or wss:// and that the domain, host and path are correct.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApiV6Text) && !StaticUtils.IsValidHttpUrl(ApiV6Text))
        {
            SettingsText = "Invalid API URL for IPv6. " +
                           "Make sure it starts with http:// or https:// and that the domain, host, port and path are correct.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApiV4Text) && !StaticUtils.IsValidHttpUrl(ApiV4Text))
        {
            SettingsText = "Invalid API URL for IPv4. " +
                           "Make sure it starts with http:// or https:// and that the domain, host, port and path are correct.";
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
}
