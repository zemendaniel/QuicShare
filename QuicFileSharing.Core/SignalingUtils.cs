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
    private readonly bool _useFixedPort;
    private readonly string _stunServer;

    private const int MinPortAmount = 5;
    
    public SignalingUtils(string stunServer, int configuredPort = 0, bool useFixedPort = false)
    {
        _configuredPort = configuredPort;
        _useFixedPort = useFixedPort;
        _stunServer = stunServer;
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
    public async Task<string> ConstructOfferAsync(string thumbprint, int poolSize = 0) 
    {
        ClientThumbprint = thumbprint;
        var ips = new HashSet<IPAddress>();
        var ports = new HashSet<int>();

        // 1. Gather local LAN IPs
        var localIps = GetLocalIps();
        foreach (var ip in localIps)
        {
            ips.Add(ip);
        }

        // 2. Concurrently reserve ports and query STUN
        // If poolSize is 0 (default), reserve as many ports as we have local IPs
        int portsToReserve = poolSize > 0 ? poolSize : Math.Max(MinPortAmount, localIps.Count);
        
        var stunTasks = new List<Task<(int LocalPort, IPEndPoint? StunEp, Socket HoldSocket)>>();
        for (int i = 0; i < portsToReserve; i++)
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
                Console.WriteLine($"[Client-Offer] Gathered STUN EP: {stunEp}");
            }
            else
            {
                Console.WriteLine($"[Client-Offer] Failed to gather STUN for local port {localPort}");
            }
        }

        var offer = new Offer
        {
            ClientIps = ips.Select(ip => ip.ToString()).ToList(),
            ClientPorts = ports.ToList(),
            ClientThumbprint = thumbprint
        };
        
        Console.WriteLine($"[Client-Offer] Generated Offer with {ips.Count} IPs and {ports.Count} Ports.");
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
        
        Console.WriteLine($"[Server-Answer] Received Offer from Client with {PeerIps.Count} IPs and {PeerPorts.Count} Ports.");

        // Server only reserves ONE port
        // Use configured port only if _useFixedPort is true, otherwise 0 (dynamic)
        int portToBind = _useFixedPort ? _configuredPort : 0;
        
        var (localPort, stunEp, socket) = await ReservePortAndStunAsync(portToBind);
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
        else
        {
             Console.WriteLine("[ICE] Server STUN failed or returned null.");
        }

        var answer = new Answer
        {
            Candidates = serverCandidates.Select(c => c.ToString()).ToList(),
            ServerThumbprint = serverThumbprint,
        };

        Console.WriteLine($"[Server-Answer] Generated Answer with {serverCandidates.Count} candidates.");
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
        Console.WriteLine($"[Client-Process] Processed Answer. Found {PeerCandidates.Count} valid candidates.");
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
            if (string.IsNullOrWhiteSpace(_stunServer)) 
            {
                Console.WriteLine("[STUN] No STUN server configured.");
                return null;
            }

            var parts = _stunServer.Split(':');
            string host = parts[0];
            int stunPort = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 19302;
            
            var stunServerAddresses = await Dns.GetHostAddressesAsync(host);
            
            // FORCE IPv4: STUN over IPv6 is rarely needed since IPv6 doesn't use NAT.
            // Furthermore, binding IPAddress.Any (IPv4) to an IPv6 STUN server causes a socket crash.
            var ipv4Server = stunServerAddresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            
            if (ipv4Server == null) 
            {
                Console.WriteLine($"[STUN] Could not resolve IPv4 address for host: {host}");
                return null;
            }

            var stunServer = new IPEndPoint(ipv4Server, stunPort);
            var localEndpoint = new IPEndPoint(IPAddress.Any, port);

            using var stunClient = new StunClient5389UDP(stunServer, localEndpoint);
            await stunClient.QueryAsync();
            var ep = stunClient.State.PublicEndPoint;
            
            if (ep != null)
                Console.WriteLine($"[STUN] Success: {localEndpoint} -> {ep}");
            else
                Console.WriteLine($"[STUN] Query failed or timeout for {localEndpoint}");
            
            return ep;
        }
        catch (Exception ex)
        { 
            Console.WriteLine($"[STUN] Error: {ex.Message}");
            return null; 
        }
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