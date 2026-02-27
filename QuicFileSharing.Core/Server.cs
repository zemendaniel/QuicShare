using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace QuicFileSharing.Core;

public class Server: QuicPeer
{
    private QuicListener? listener;
    
    public async Task StartAsync(int localPort, string expectedThumbprint, List<IPAddress> clientIps, List<int> clientPorts)    {
        Console.WriteLine($"[Server] Starting cross-product hole punching from singular port {localPort}...");
        
        using (var udpClient = new UdpClient(AddressFamily.InterNetworkV6))
        {
            udpClient.Client.DualMode = true;
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));

            var dummyPacket = new byte[] { 0xFF }; 

            // CROSS-PRODUCT: Loop through every IP and pair it with every Port
            foreach (var ip in clientIps)
            {
                foreach (var port in clientPorts)
                {
                    var target = new IPEndPoint(ip, port);
                    for (int i = 0; i < 3; i++) // 3 packets for redundancy
                    {
                        try { await udpClient.SendAsync(dummyPacket, dummyPacket.Length, target); }
                        catch { /* Ignore unreachable routes */ }
                    }
                }
            }
        } 

        Console.WriteLine("[Server] Hole punching complete. Transitioning to QUIC Listener...");

        var listenEndpoint = new IPEndPoint(IPAddress.IPv6Any, localPort);
        var serverConnectionOptions = new QuicServerConnectionOptions
        {
            IdleTimeout = connectionTimeout,
            KeepAliveInterval = keepAliveInterval,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol("fileShare")],
                ServerCertificate = cert,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is X509Certificate2 clientCert)
                    {
                        return clientCert.Thumbprint.Equals(expectedThumbprint, StringComparison.OrdinalIgnoreCase); 
                    }
                    return false;
                }
            }
        };

        listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = listenEndpoint,
            ApplicationProtocols = [new SslApplicationProtocol("fileShare")],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
        });
        
        Console.WriteLine($"[Server] QUIC Server actively listening on {listenEndpoint}");
        
        cts = new CancellationTokenSource();
        token = cts.Token;
        
        _ = Task.Run(AcceptConnection, token);
    }
    
    private async Task AcceptConnection()
    {
        try
        {
            if (listener == null)
                throw new InvalidOperationException("Listener not initialized.");
            try
            {
                connection = await listener.AcceptConnectionAsync(token);
            }
            catch (AuthenticationException)
            {
                GotConnectedToPeer.SetResult(false);
                CallOnDisconnected("Your peer has provided an invalid certificate.");
                await StopAsync();
                return;
            }

            GotConnectedToPeer.SetResult(true);
            Console.WriteLine($"Accepted connection from {connection.RemoteEndPoint}");
            _ = Task.Run(HandleStreamsAsync, token);
            _ = Task.Run(TimeoutCheckLoopAsync, token);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    private async Task HandleStreamsAsync()
    {
        if (connection == null)
            throw new InvalidOperationException("Connection not initialized.");
        try
        {
            while (!token.IsCancellationRequested && !bothStreamsReady.Task.IsCompleted)
            {
                try
                {
                    var stream = await connection.AcceptInboundStreamAsync(token);
                    var header = new byte[1];
                    var bytesRead = await stream.ReadAsync(header.AsMemory(), token);
                    if (bytesRead == 0)
                        continue;
                
                    // I could use the stream IDs, but I prefer more control here. 
                    switch (header[0])
                    {
                        case 0x01:
                            controlStream = stream;  
                            _ = Task.Run(ControlLoopAsync, token);
                            // Console.WriteLine("Opened control stream");
                            SetControlStream();
                            break;

                        case 0x02:
                            fileStream = stream;     
                            // Console.WriteLine("Opened file stream");
                            SetFileStream();
                            break;

                        default:
                            await stream.DisposeAsync();
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuicException ex) when (ex.InnerException == null || ex.Message.Contains("timed out"))
                {
                    // Console.WriteLine("Connection timed out due to inactivity.");
                    await StopAsync();
                } 
            }
        }
        catch (OperationCanceledException)
        {
            // Console.WriteLine("Connection cancelled.");
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"Connection error: {ex}");
        }
    }

    public override async Task StopAsync()
    {
        if (cts != null)
            await cts.CancelAsync(); 

        if (listener != null)
            await listener.DisposeAsync();
        
        if (connection != null)
            await connection.DisposeAsync();

        Console.WriteLine("Server stopped.");
    }
}
