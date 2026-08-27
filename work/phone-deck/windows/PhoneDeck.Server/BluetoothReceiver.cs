using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal sealed class BluetoothReceiver : IAsyncDisposable
{
    private static readonly Guid ServiceUuid = new("7d2ea28a-f7bd-485a-bd9d-92ad6ecfe93e");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource ownCancellation = new();
    private readonly string computerId;
    private readonly string displayName;
    private Task? worker;
    private IntPtr activeSocket = BluetoothNative.InvalidSocket;
    private string lastStatus = string.Empty;

    internal BluetoothReceiver(string computerId, string displayName)
    {
        this.computerId = computerId;
        this.displayName = displayName;
    }

    internal void Start(CancellationToken applicationStopping)
    {
        if (worker is not null)
        {
            return;
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ownCancellation.Token, applicationStopping);
        worker = Task.Run(() => Run(linked.Token), linked.Token);
    }

    private void Run(CancellationToken cancellationToken)
    {
        if (!BluetoothNative.StartSockets())
        {
            WriteStatus("蓝牙：Windows 蓝牙套接字初始化失败；USB 仍可使用。");
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var devices = BluetoothNative.GetPairedClassicDevices();
                if (devices.Count == 0)
                {
                    WriteStatus("蓝牙：尚未发现已配对的手机，USB 不受影响。");
                    Wait(cancellationToken, 2500);
                    continue;
                }

                var connected = false;
                foreach (var device in PrioritizePhones(devices))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    if (ConnectAndReceive(device, cancellationToken))
                    {
                        connected = true;
                        break;
                    }
                }

                if (!connected)
                {
                    WriteStatus("蓝牙：等待手机上的 PhoneDeck 服务…");
                    Wait(cancellationToken, 1800);
                }
            }
        }
        finally
        {
            CloseActiveSocket();
            BluetoothNative.StopSockets();
        }
    }

    private bool ConnectAndReceive(BluetoothDevice device, CancellationToken cancellationToken)
    {
        var socket = BluetoothNative.CreateRfcommSocket();
        if (socket == BluetoothNative.InvalidSocket)
        {
            return false;
        }

        activeSocket = socket;
        try
        {
            if (!BluetoothNative.Connect(socket, device.Address, ServiceUuid))
            {
                return false;
            }

            WriteStatus($"蓝牙：已连接 {device.Name}");
            BluetoothNative.SetReceiveTimeout(socket, 2500);
            SendHello(socket, computerId, displayName);

            var received = new byte[4096];
            var pending = new List<byte>(4096);
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = BluetoothNative.Receive(socket, received);
                if (count > 0)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var value = received[index];
                        if (value == (byte)'\n')
                        {
                            HandleMessage(socket, pending, computerId);
                            pending.Clear();
                        }
                        else if (pending.Count < 32 * 1024)
                        {
                            pending.Add(value);
                        }
                        else
                        {
                            pending.Clear();
                        }
                    }
                    continue;
                }

                if (count == 0)
                {
                    break;
                }

                if (BluetoothNative.LastError == BluetoothNative.SocketTimedOut)
                {
                    if (!BluetoothNative.SendHeartbeat(socket))
                    {
                        break;
                    }
                    continue;
                }
                break;
            }
        }
        finally
        {
            if (activeSocket == socket)
            {
                activeSocket = BluetoothNative.InvalidSocket;
            }
            BluetoothNative.Close(socket);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            WriteStatus($"蓝牙：{device.Name} 已断开，正在重连…");
        }
        return false;
    }

    private static void HandleMessage(IntPtr socket, List<byte> bytes, string computerId)
    {
        if (bytes.Count == 0)
        {
            return;
        }
        string? requestId = null;
        try
        {
            var json = Encoding.UTF8.GetString(bytes.ToArray());
            var command = JsonSerializer.Deserialize<InputCommand>(json, JsonOptions);
            if (command is null)
            {
                return;
            }
            requestId = command.RequestId;
            var result = InputCommandProcessor.Execute(command, computerId);
            SendAcknowledgement(socket, command.RequestId, true,
                result.Duplicate, null, computerId, result.Message);
        }
        catch (JsonException)
        {
            // 丢弃损坏的数据包，保持蓝牙会话继续运行。
        }
        catch (ArgumentException exception)
        {
            // 丢弃未知动作，不允许手机端调用任意电脑命令。
            SendAcknowledgement(socket, requestId, false, false,
                exception.Message, computerId, null);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"蓝牙输入失败：{exception.Message}");
            SendAcknowledgement(socket, requestId, false, false,
                "电脑端执行失败", computerId, null);
        }
    }

    private static void SendHello(IntPtr socket, string computerId, string displayName)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "hello",
            name = "PhoneDeck",
            version = "1.6.0-dev.2",
            protocolVersion = 2,
            computerId,
            displayName,
            platform = "windows",
            capabilities = new[] { "fixedAction", "keyChord", "text" }
        });
        BluetoothNative.SendMessage(socket, Encoding.UTF8.GetBytes(json + "\n"));
    }

    private static void SendAcknowledgement(
        IntPtr socket,
        string? requestId,
        bool ok,
        bool duplicate,
        string? error,
        string computerId,
        string? message)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }
        var json = JsonSerializer.Serialize(new
        {
            type = "ack",
            requestId,
            ok,
            duplicate,
            error,
            computerId,
            message
        });
        BluetoothNative.SendMessage(socket,
            Encoding.UTF8.GetBytes(json + "\n"));
    }

    private static IEnumerable<BluetoothDevice> PrioritizePhones(IEnumerable<BluetoothDevice> devices)
    {
        string[] phoneHints =
        [
            "phone", "galaxy", "samsung", "pixel", "xiaomi", "redmi", "oppo",
            "vivo", "oneplus", "huawei", "honor", "motorola", "手机", "s20"
        ];

        return devices.OrderByDescending(device =>
            phoneHints.Any(hint => device.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)));
    }

    private void WriteStatus(string status)
    {
        if (status == lastStatus)
        {
            return;
        }
        lastStatus = status;
        Console.WriteLine(status);
    }

    private static void Wait(CancellationToken token, int milliseconds)
    {
        token.WaitHandle.WaitOne(milliseconds);
    }

    private void CloseActiveSocket()
    {
        var socket = Interlocked.Exchange(ref activeSocket, BluetoothNative.InvalidSocket);
        if (socket != BluetoothNative.InvalidSocket)
        {
            BluetoothNative.Close(socket);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ownCancellation.Cancel();
        CloseActiveSocket();
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 进程退出时不让蓝牙驱动延迟关闭。
            }
        }
        ownCancellation.Dispose();
    }
}

internal sealed record BluetoothDevice(ulong Address, string Name);

internal static class BluetoothNative
{
    internal static readonly IntPtr InvalidSocket = new(-1);
    internal const int SocketTimedOut = 10060;

    private const int AddressFamilyBluetooth = 32;
    private const int SocketStream = 1;
    private const int ProtocolRfcomm = 3;
    private const int SocketError = -1;
    private const int SolSocket = 0xFFFF;
    private const int SoReceiveTimeout = 0x1006;

    internal static int LastError => WSAGetLastError();

    internal static bool StartSockets()
    {
        return WSAStartup(0x0202, out _) == 0;
    }

    internal static void StopSockets()
    {
        WSACleanup();
    }

    internal static IReadOnlyList<BluetoothDevice> GetPairedClassicDevices()
    {
        var devices = new List<BluetoothDevice>();
        var search = new BluetoothDeviceSearchParameters
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceSearchParameters>(),
            ReturnAuthenticated = 1,
            ReturnRemembered = 1,
            ReturnUnknown = 0,
            ReturnConnected = 1,
            IssueInquiry = 0,
            TimeoutMultiplier = 2,
            RadioHandle = IntPtr.Zero
        };
        var info = NewDeviceInfo();
        var find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero)
        {
            return devices;
        }

        try
        {
            do
            {
                var name = string.IsNullOrWhiteSpace(info.Name)
                    ? info.Address.ToString("X12")
                    : info.Name.TrimEnd('\0').Trim();
                if (info.Authenticated != 0 || info.Remembered != 0)
                {
                    devices.Add(new BluetoothDevice(info.Address, name));
                }
                info = NewDeviceInfo();
            }
            while (BluetoothFindNextDevice(find, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }

        return devices;
    }

    internal static IntPtr CreateRfcommSocket()
    {
        return WSASocketW(AddressFamilyBluetooth, SocketStream, ProtocolRfcomm,
            IntPtr.Zero, 0, 0);
    }

    internal static bool Connect(IntPtr socket, ulong address, Guid serviceUuid)
    {
        var endpoint = new SocketAddressBluetooth
        {
            AddressFamily = AddressFamilyBluetooth,
            BluetoothAddress = address,
            ServiceClassId = serviceUuid,
            Port = 0
        };
        return connect(socket, ref endpoint, Marshal.SizeOf<SocketAddressBluetooth>()) == 0;
    }

    internal static void SetReceiveTimeout(IntPtr socket, int milliseconds)
    {
        setsockopt(socket, SolSocket, SoReceiveTimeout, ref milliseconds, sizeof(int));
    }

    internal static int Receive(IntPtr socket, byte[] buffer)
    {
        return recv(socket, buffer, buffer.Length, 0);
    }

    internal static bool SendHeartbeat(IntPtr socket)
    {
        byte[] heartbeat = [(byte)'\n'];
        return SendMessage(socket, heartbeat);
    }

    internal static bool SendMessage(IntPtr socket, byte[] message)
    {
        var total = 0;
        while (total < message.Length)
        {
            var remaining = total == 0 ? message : message[total..];
            var sent = send(socket, remaining, remaining.Length, 0);
            if (sent <= 0)
            {
                return false;
            }
            total += sent;
        }
        return true;
    }

    internal static void Close(IntPtr socket)
    {
        if (socket != InvalidSocket && socket != IntPtr.Zero)
        {
            closesocket(socket);
        }
    }

    private static BluetoothDeviceInformation NewDeviceInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<BluetoothDeviceInformation>(),
        Name = string.Empty
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SocketAddressBluetooth
    {
        public int AddressFamily;
        public ulong BluetoothAddress;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParameters
    {
        public uint Size;
        public int ReturnAuthenticated;
        public int ReturnRemembered;
        public int ReturnUnknown;
        public int ReturnConnected;
        public int IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr RadioHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInformation
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct WsaData
    {
        public ushort Version;
        public ushort HighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string SystemStatus;
        public ushort MaxSockets;
        public ushort MaxUdpDatagram;
        public IntPtr VendorInfo;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort versionRequested, out WsaData data);

    [DllImport("ws2_32.dll")]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WSASocketW(int addressFamily, int socketType, int protocol,
        IntPtr protocolInfo, uint group, uint flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr socket, ref SocketAddressBluetooth name, int nameLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(IntPtr socket, [Out] byte[] buffer, int length, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(IntPtr socket, byte[] buffer, int length, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr socket, int level, int optionName,
        ref int optionValue, int optionLength);

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(IntPtr socket);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParameters searchParameters,
        ref BluetoothDeviceInformation deviceInformation);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        IntPtr findHandle,
        ref BluetoothDeviceInformation deviceInformation);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr findHandle);
}
