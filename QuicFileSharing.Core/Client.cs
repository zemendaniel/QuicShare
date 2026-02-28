using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace QuicFileSharing.Core;

public class Client : QuicPeer
{
    public async Task StartAsync(List<IPEndPoint> serverCandidates, string expectedThumbprint, List<int> reservedPorts)
    {
        Console.WriteLine($"[Client] Starting connection race using {reservedPorts.Count} reserved ports...");
        
        cts = new CancellationTokenSource();
        token = cts.Token;

        var clientAuthOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol("fileShare")],
            TargetHost = "quicshare-peer", 
            ClientCertificates = new X509CertificateCollection { cert },
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is X509Certificate2 serverCert)
                {
                    return string.Equals(serverCert.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
        };

        try
        {
            connection = await RaceConnectionsAsync(serverCandidates, reservedPorts, clientAuthOptions);
        }
        catch (OperationCanceledException) { /* Handled */ }

        if (connection == null)
        {
            GotConnectedToPeer.SetResult(false);
            // todo change text
            CallOnDisconnected("Failed to connect to peer. NAT pinhole may have failed or peer is unreachable.\nCheck your firewall or try configuring IPv4 Port Forwarding.");
            await StopAsync();
            return;
        }

        Console.WriteLine($"[Client] RACE WON! Connected to {connection.RemoteEndPoint}");

        // 2. Open Streams
        controlStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
        await controlStream.WriteAsync(new byte[] { 0x01 }, token);
        SetControlStream();
        _ = Task.Run(ControlLoopAsync, token);
        
        fileStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
        await fileStream.WriteAsync(new byte[] { 0x02 }, token); 
        SetFileStream();

        GotConnectedToPeer.SetResult(true);
        _ = Task.Run(TimeoutCheckLoopAsync, token);
    }
    private async Task<QuicConnection?> RaceConnectionsAsync(List<IPEndPoint> candidates, List<int> reservedPorts, SslClientAuthenticationOptions authOptions)
    {
        // todo make sure there are no race conditions
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        raceCts.CancelAfter(TimeSpan.FromSeconds(10)); 
        
        QuicConnection? winningConnection = null;
        var pendingTasks = new List<Task<QuicConnection?>>();

        // Assign exactly one unique reserved port to each Server Candidate
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i >= reservedPorts.Count)
            {
                Console.WriteLine($"[Client] Out of reserved ports, skipping remaining candidate: {candidates[i]}");
                break;
            }

            int distinctLocalPort = reservedPorts[i];
            
            pendingTasks.Add(AttemptSingleConnectionAsync(
                candidates[i], 
                distinctLocalPort, 
                authOptions, 
                raceCts.Token));
        }

        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(completedTask);

            try
            {
                var conn = await completedTask; 
                if (conn == null) continue;
                // todo what is this?
                if (Interlocked.CompareExchange(ref winningConnection, conn, null) == null)
                {
                    await raceCts.CancelAsync();
                    return winningConnection; 
                }
                else
                {
                    await conn.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in connection attempt: {ex.Message}");
            }
        }

        return null; 
    }
    private async Task<QuicConnection?> AttemptSingleConnectionAsync(IPEndPoint remoteEndpoint, int localPort, SslClientAuthenticationOptions authOptions, CancellationToken cancellationToken)
    {
        // Bind to Any/IPv6Any so the OS routing table can pick the correct physical interface
        var localIp = remoteEndpoint.AddressFamily == AddressFamily.InterNetwork 
            ? IPAddress.Any 
            : IPAddress.IPv6Any;

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = remoteEndpoint,
            LocalEndPoint = new IPEndPoint(localIp, localPort), // Distinct port per task prevents MSQuic crash
            IdleTimeout = connectionTimeout,
            KeepAliveInterval = keepAliveInterval,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ClientAuthenticationOptions = authOptions
        };

        try
        {
            Console.WriteLine($"[Client] Racing candidate: {remoteEndpoint} from local port {localPort}...");
            return await QuicConnection.ConnectAsync(options, cancellationToken);
        }
        catch
        {
            return null; // Connection failed (timeout, refused, cancelled)
        }
    }


    public override async Task StopAsync()
    {
        if (cts != null)
            await cts.CancelAsync(); 
        
        if (connection != null)
            await connection.DisposeAsync();

        Console.WriteLine("Client stopped.");
    }
}
