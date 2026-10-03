using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SoulsProx.Proximity;

namespace SoulsProx.Net;

public enum LinkState
{
    /// <summary>No friend code entered.</summary>
    Idle,
    /// <summary>Sending hole-punch packets, waiting to hear from the friend.</summary>
    Connecting,
    /// <summary>Exchanging packets with the friend.</summary>
    Connected,
}

/// <summary>
/// One UDP socket shared by STUN discovery and the encrypted peer-to-peer link to the friend.
/// Both players send to each other at the same time, which opens a path through most home routers.
/// </summary>
public sealed class PeerLink : IDisposable
{
    private static readonly TimeSpan FastPunchInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowPunchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FastPunchFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(8);
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

    private readonly Socket _socket;
    private readonly byte[] _secret;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _receiveThread;
    private readonly Thread _timerThread;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _stunPending = new();

    private PacketCrypto? _crypto;
    private IPEndPoint[] _candidates = [];
    private IPEndPoint? _remote;
    private DateTime _lastHeard, _connectStarted, _lastPunch, _lastKeepAlive;
    private volatile LinkState _state = LinkState.Idle;

    public PeerLink(int preferredPort, byte[] secret)
    {
        _secret = secret;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Stop Windows from failing the next ReceiveFrom when an ICMP "port unreachable" comes back.
        _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        try { _socket.Bind(new IPEndPoint(IPAddress.Any, preferredPort)); }
        catch (SocketException) { _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }

        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        LanEndpoint = new IPEndPoint(DetectLanAddress(), LocalPort);

        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "SoulsProx net receive" };
        _timerThread = new Thread(TimerLoop) { IsBackground = true, Name = "SoulsProx net timer" };
        _receiveThread.Start();
        _timerThread.Start();
    }

    public int LocalPort { get; }
    public IPEndPoint LanEndpoint { get; }
    public IPEndPoint? PublicEndpoint { get; private set; }

    /// <summary>Human-readable note about NAT / STUN results, empty if all looks fine.</summary>
    public string NatNote { get; private set; } = "";

    public LinkState State => _state;
    public IPEndPoint? RemoteEndpoint => _remote;
    public ConnectionCode MyCode => new(PublicEndpoint, LanEndpoint, _secret);

    public event Action<LinkState>? StateChanged;
    public event Action<PeerReport>? ReportReceived;
    public event Action<uint, ReadOnlyMemory<byte>, bool>? VoiceReceived;

    /// <summary>Learns our public address from STUN servers so it can go into our code.</summary>
    public async Task DiscoverPublicEndpointAsync(CancellationToken ct = default)
    {
        var tasks = Stun.Servers.Select(s => QueryStunAsync(s.Host, s.Port, ct)).ToList();
        var results = (await Task.WhenAll(tasks)).OfType<IPEndPoint>().ToList();
        if (results.Count == 0)
        {
            NatNote = "Couldn't reach the internet address lookup (STUN). Your code only has your local network address.";
            return;
        }
        PublicEndpoint = results[0];
        NatNote = results.Select(r => r.Port).Distinct().Count() > 1
            ? "Your router changes ports per destination (strict/symmetric NAT). Codes may not connect; see the README for alternatives."
            : "";
    }

    private async Task<IPEndPoint?> QueryStunAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            if (addresses.Length == 0) return null;
            var server = new IPEndPoint(addresses[0], port);
            var request = Stun.CreateBindingRequest(out var txId);
            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            string key = Convert.ToHexString(txId);
            _stunPending[key] = tcs;
            try
            {
                for (int attempt = 0; attempt < 3 && !tcs.Task.IsCompleted; attempt++)
                {
                    _socket.SendTo(request, server);
                    await Task.WhenAny(tcs.Task, Task.Delay(700, ct));
                }
                return tcs.Task.IsCompleted ? tcs.Task.Result : null;
            }
            finally
            {
                _stunPending.TryRemove(key, out _);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Starts (or restarts) connecting to the friend.</summary>
    /// <param name="overrideEndpoint">Send here instead of the addresses in the code (port forwarding, Tailscale, …).</param>
    public void Connect(ConnectionCode friend, IPEndPoint? overrideEndpoint = null)
    {
        if (friend.Secret.AsSpan().SequenceEqual(_secret))
            throw new ArgumentException("That's your own code. Paste your friend's code instead.");

        lock (_lock)
        {
            var old = _crypto;
            _crypto = new PacketCrypto(_secret, friend.Secret);
            old?.Dispose();
            _candidates = overrideEndpoint is not null
                ? [overrideEndpoint]
                : new[] { friend.Lan, friend.Public }.OfType<IPEndPoint>().Distinct().ToArray();
            _remote = null;
            _connectStarted = DateTime.UtcNow;
            _lastPunch = DateTime.MinValue;
        }
        SetState(LinkState.Connecting);
    }

    public void Disconnect()
    {
        if (_state == LinkState.Connected && _remote is { } remote) Send(PacketType.Bye, [], remote);
        lock (_lock)
        {
            _crypto?.Dispose();
            _crypto = null;
            _candidates = [];
            _remote = null;
        }
        SetState(LinkState.Idle);
    }

    public void SendVoice(uint seq, ReadOnlySpan<byte> opus, bool radio)
    {
        if (_state == LinkState.Connected && _remote is { } remote)
            Send(PacketType.Voice, Payloads.EncodeVoice(seq, radio, opus), remote);
    }

    public void SendState(PeerReport report)
    {
        if (_state == LinkState.Connected && _remote is { } remote)
            Send(PacketType.State, Payloads.EncodeState(report), remote);
    }

    private void Send(PacketType type, ReadOnlySpan<byte> payload, IPEndPoint to)
    {
        byte[] packet;
        lock (_lock)
        {
            if (_crypto is null) return;
            packet = _crypto.Seal(type, payload);
        }
        try { _socket.SendTo(packet, to); }
        catch (SocketException) { /* transient; the timer will retry */ }
        catch (ObjectDisposedException) { }
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = _socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; continue; }
            catch (ObjectDisposedException) { return; }

            var data = buffer.AsSpan(0, n);
            var sender = (IPEndPoint)from;
            if (Stun.IsStunMessage(data)) HandleStun(data);
            else if (PacketCrypto.LooksLikePacket(data)) HandlePacket(data, sender);
        }
    }

    private void HandleStun(ReadOnlySpan<byte> data)
    {
        if (Stun.TryParseBindingResponse(data, out var txId, out var mapped) && mapped is not null
            && _stunPending.TryGetValue(Convert.ToHexString(txId), out var tcs))
            tcs.TrySetResult(mapped);
    }

    private void HandlePacket(ReadOnlySpan<byte> data, IPEndPoint sender)
    {
        PacketType type;
        byte[] payload;
        lock (_lock)
        {
            if (_crypto is null || !_crypto.TryOpen(data, out type, out payload)) return;
            _lastHeard = DateTime.UtcNow;
        }

        if (type == PacketType.Bye)
        {
            lock (_lock)
            {
                _remote = null;
                _connectStarted = DateTime.UtcNow;
            }
            SetState(LinkState.Connecting);
            return;
        }

        // Authenticated, so it's safe to follow the friend to whatever address they now come from.
        if (_state != LinkState.Connected || !sender.Equals(_remote))
        {
            _remote = sender;
            SetState(LinkState.Connected);
        }

        switch (type)
        {
            case PacketType.Hello:
                Send(PacketType.HelloAck, [], sender);
                break;
            case PacketType.State when Payloads.TryDecodeState(payload, out var report):
                ReportReceived?.Invoke(report!);
                break;
            case PacketType.Voice when Payloads.TryDecodeVoice(payload, out uint seq, out bool radio, out var opus):
                VoiceReceived?.Invoke(seq, opus, radio);
                break;
        }
    }

    private void TimerLoop()
    {
        while (!_cts.Token.WaitHandle.WaitOne(50))
        {
            var now = DateTime.UtcNow;
            IPEndPoint[] punchTargets = [];
            IPEndPoint? keepAliveTarget = null;
            bool lost = false;

            lock (_lock)
            {
                if (_crypto is null) continue;
                if (_state == LinkState.Connecting)
                {
                    var interval = now - _connectStarted < FastPunchFor ? FastPunchInterval : SlowPunchInterval;
                    if (now - _lastPunch >= interval)
                    {
                        _lastPunch = now;
                        punchTargets = _candidates;
                    }
                }
                else if (_state == LinkState.Connected)
                {
                    if (now - _lastHeard > LinkTimeout)
                    {
                        _remote = null;
                        _connectStarted = now;
                        lost = true;
                    }
                    else if (now - _lastKeepAlive >= KeepAliveInterval)
                    {
                        _lastKeepAlive = now;
                        keepAliveTarget = _remote;
                    }
                }
            }

            if (lost) SetState(LinkState.Connecting);
            foreach (var target in punchTargets) Send(PacketType.Hello, [], target);
            if (keepAliveTarget is not null) Send(PacketType.Hello, [], keepAliveTarget);
        }
    }

    private void SetState(LinkState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    private static IPAddress DetectLanAddress()
    {
        try
        {
            // Connecting a UDP socket sends nothing; it just makes Windows pick the outgoing interface.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return IPAddress.Loopback;
        }
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Cancel();
        _socket.Dispose();
        _timerThread.Join(TimeSpan.FromSeconds(1));
        _receiveThread.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }
}
