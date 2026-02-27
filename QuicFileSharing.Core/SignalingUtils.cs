using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using STUN.Client;

namespace QuicFileSharing.Core;

public class Offer
{
    public required List<string> ClientIps { get; init; }
    public required List<int> ClientPorts { get; init; }
    public required string ClientThumbprint { get; init; }
}

public class Answer
{
    public required List<string> Candidates { get; init; }
    public required string ServerThumbprint { get; init; }
}

public class RoomInfo
{
    public required string id { get; init; } 
    public required int ex { get; init; }
}

public class SignalingMessage
{
    public required string Type { get; init; } 
    public required string Data { get; init; }
}

public class SignalingUtils : IDisposable
{
    private readonly int _configuredPort;
    
    public SignalingUtils(int configuredPort = 0)
    {
        _configuredPort = configuredPort;
    }
    
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Client-side properties
    public List<int> ReservedPorts { get; private set; } = [];
    
    // Server-side properties
    public List<IPAddress> PeerIps { get; private set; } = [];
    public List<int> PeerPorts { get; private set; } = [];
    
    // Shared properties
    public List<IPEndPoint> PeerCandidates { get; private set; } = [];
    public int LocalPort { get; private set; }
    public string? ClientThumbprint { get; private set; }
    public string? ServerThumbprint { get; private set; }
    
    private readonly List<Socket> holdSockets = [];

    // ====== CLIENT SIDE GATHERING ======
    public async Task<string> ConstructOfferAsync(string thumbprint, int poolSize = 10) // 10 ports is plenty for the race
    {
        ClientThumbprint = thumbprint;
        var ips = new HashSet<IPAddress>();
        var ports = new HashSet<int>();

        // 1. Gather local LAN IPs
        foreach (var ip in GetLocalIps())
        {
            ips.Add(ip);
        }

        // 2. Concurrently reserve ports and query STUN
        var stunTasks = new List<Task<(int LocalPort, IPEndPoint? StunEp, Socket HoldSocket)>>();
        for (int i = 0; i < poolSize; i++)
        {
            stunTasks.Add(ReservePortAndStunAsync(0)); // Dynamic ports for Client
        }

        var results = await Task.WhenAll(stunTasks);

        foreach (var (localPort, stunEp, socket) in results)
        {
            ReservedPorts.Add(localPort);
            ports.Add(localPort);
            holdSockets.Add(socket);

            if (stunEp != null)
            {
                ips.Add(stunEp.Address);
                ports.Add(stunEp.Port); // Include the NAT translated port in the pool
            }
        }

        var offer = new Offer
        {
            ClientIps = ips.Select(ip => ip.ToString()).ToList(),
            ClientPorts = ports.ToList(),
            ClientThumbprint = thumbprint
        };
        
        return JsonSerializer.Serialize(offer);
    }

    // ====== SERVER SIDE GATHERING ======
    public async Task<string> ConstructAnswerAsync(string offerJson, string serverThumbprint)
    {
        var offer = JsonSerializer.Deserialize<Offer>(offerJson, Options) 
            ?? throw new ArgumentException("Invalid offer JSON");

        ClientThumbprint = offer.ClientThumbprint;
        PeerIps = offer.ClientIps.Select(IPAddress.Parse).ToList();
        PeerPorts = offer.ClientPorts;
        ServerThumbprint = serverThumbprint;

        // Server only reserves ONE port (Configured Port or Dynamic)
        var (localPort, stunEp, socket) = await ReservePortAndStunAsync(_configuredPort);
        LocalPort = localPort;
        holdSockets.Add(socket);

        var serverCandidates = new List<IPEndPoint>();
        
        // Pair Server LAN IPs with the single Server Local Port
        foreach (var ip in GetLocalIps())
        {
            serverCandidates.Add(new IPEndPoint(ip, LocalPort));
        }

        if (stunEp != null)
        {
            serverCandidates.Add(stunEp);
            Console.WriteLine($"[ICE] Server STUN Public Endpoint: {stunEp}");
        }

        var answer = new Answer
        {
            Candidates = serverCandidates.Select(c => c.ToString()).ToList(),
            ServerThumbprint = serverThumbprint,
        };

        return JsonSerializer.Serialize(answer);
    }

    public void ProcessAnswer(string answerJson)
    {
        var answer = JsonSerializer.Deserialize<Answer>(answerJson, Options) 
            ?? throw new ArgumentException("Invalid answer JSON");

        ServerThumbprint = answer.ServerThumbprint;
        PeerCandidates.Clear();
        foreach (var epString in answer.Candidates)
        {
            if (IPEndPoint.TryParse(epString, out var endpoint))
            {
                PeerCandidates.Add(endpoint);
            }
        }
    }

    private List<IPAddress> GetLocalIps()
    {
        var ips = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (!IsApipaOrLinkLocal(ip.Address)) 
                {
                    ips.Add(ip.Address);
                }
            }
        }
        return ips;
    }

    // Helper: Safely binds, gets STUN, and re-binds to hold the NAT pinhole
    private async Task<(int LocalPort, IPEndPoint? PublicEp, Socket HoldSocket)> ReservePortAndStunAsync(int targetPort)
    {
        int actualPort;
        using (var tempSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
        {
            tempSocket.DualMode = true;
            tempSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, targetPort));
            actualPort = ((IPEndPoint)tempSocket.LocalEndPoint!).Port;
        }

        var stunEp = await GetStunEndpointAsync(actualPort);

        var holdSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        holdSocket.DualMode = true;
        holdSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        holdSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, actualPort));

        return (actualPort, stunEp, holdSocket);
    }

    private async Task<IPEndPoint?> GetStunEndpointAsync(int port)
    {
        try
        {
            var stunServerAddresses = await Dns.GetHostAddressesAsync("stun.l.google.com");
            if (stunServerAddresses.Length == 0) return null;

            var stunServer = new IPEndPoint(stunServerAddresses[0], 19302);
            var localEndpoint = new IPEndPoint(IPAddress.Any, port);

            using var stunClient = new StunClient5389UDP(stunServer, localEndpoint);
            await stunClient.QueryAsync();
            return stunClient.State.PublicEndPoint;
        }
        catch { return null; }
    }

    private bool IsApipaOrLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback);
        }
        return false;
    }

    public void ReleaseHoldSockets()
    {
        foreach (var socket in holdSockets)
        {
            socket.Close();
            socket.Dispose();
        }
        holdSockets.Clear();
    }

    public void Dispose() => ReleaseHoldSockets();
}