using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PhoneDeck.MacReceiver;

internal sealed class LanDiscoveryResponder : IDisposable
{
    internal const int DiscoveryPort = 8767;
    internal const string Magic = "PHONEDECK-DISCOVER";

    private readonly UdpClient? client;
    private readonly byte[] responsePayload;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread worker;

    internal LanDiscoveryResponder(
        ReceiverIdentity identity,
        int httpsPort,
        IReadOnlyCollection<string> capabilities)
    {
        try
        {
            client = new UdpClient(DiscoveryPort);
            client.Client.ReceiveTimeout = 500;
        }
        catch (SocketException exception)
        {
            Console.WriteLine($"局域网发现监听未启用（UDP {DiscoveryPort}）：{exception.Message}");
        }
        responsePayload = JsonSerializer.SerializeToUtf8Bytes(
            new LanDiscoveryReply(true, "phonedeck", 2, identity.ComputerId, identity.DisplayName,
                httpsPort, identity.Platform, capabilities),
            MacApiJsonContext.Default.LanDiscoveryReply);
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "PhoneDeckMacDiscovery"
        };
    }

    private volatile bool enabled = true;
    internal bool PortBound => client is not null;
    internal bool Running => enabled && worker.IsAlive;

    /// <summary>手机集中设置可随时开关；关闭时不再应答发现请求。</summary>
    internal void SetEnabled(bool value)
    {
        enabled = value;
        if (value)
        {
            Start();
        }
    }

    internal void Start()
    {
        if (client is not null && !worker.IsAlive)
        {
            worker.Start();
            Console.WriteLine($"局域网发现在线：UDP {DiscoveryPort}（仅应答，不泄露令牌）");
        }
    }

    private void Run()
    {
        while (!cancellation.IsCancellationRequested)
        {
            IPEndPoint? remote = null;
            byte[] data;
            try
            {
                remote = new IPEndPoint(IPAddress.Any, 0);
                data = client!.Receive(ref remote);
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            if (!enabled || remote is null || data.Length > 64)
            {
                continue;
            }
            var text = Encoding.ASCII.GetString(data).TrimEnd('\0', ' ', '\r', '\n');
            if (!string.Equals(text, Magic, StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                client!.Send(responsePayload, responsePayload.Length, remote);
            }
            catch (Exception)
            {
                // 单次应答失败不影响后续发现。
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        client?.Dispose();
    }
}
