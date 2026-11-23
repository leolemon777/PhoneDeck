package com.codex.phonedeck;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.wifi.WifiManager;

import org.json.JSONObject;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.SocketTimeoutException;
import java.util.Collections;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * 局域网发现客户端（M1-A A5 双通道）：UDP 广播 8767 + 系统 NSD（mDNS _phonedeck._tcp），
 * 两路结果按 computerId 合并。应答只携带 computerId、名称、端口与能力；
 * 建立控制连接仍必须走 HTTPS 8766 的令牌与证书固定校验，本类不触碰任何配对密钥。
 */
final class LanDiscoveryClient {
    static final int DISCOVERY_PORT = 8767;
    private static final String MAGIC = "PHONEDECK-DISCOVER";
    private static final int MAX_PACKET_BYTES = 2048;
    private static final String NSD_SERVICE_TYPE = "_phonedeck._tcp.";
    /// 广播后等这么久仍无任何 UDP 应答，才退回逐台单播扫描。
    static final long SWEEP_AFTER_SILENT_MS = 300;

    static final class DiscoveredComputer {
        final String computerId;
        final String displayName;
        final String hostAddress;
        final int port;
        /// "windows" / "macos" / "unknown"（来自 TXT 或 UDP 应答）。
        final String platform;

        DiscoveredComputer(String computerId, String displayName,
                           String hostAddress, int port) {
            this(computerId, displayName, hostAddress, port, "unknown");
        }

        DiscoveredComputer(String computerId, String displayName,
                           String hostAddress, int port, String platform) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.hostAddress = hostAddress;
            this.port = port;
            this.platform = platform == null || platform.isBlank() ? "unknown" : platform;
        }
    }

    private LanDiscoveryClient() {
    }

    /// 手机当前局域网的前缀（如 "192.168.1."）；取不到时返回 null。
    static String currentSubnetPrefix() {
        java.util.List<String> prefixes = activeSubnetPrefixes();
        return prefixes.isEmpty() ? null : prefixes.get(0);
    }

    /// 手机所有活动的私有 IPv4 网段前缀（如 ["192.168.0."]），过滤回环、APIPA 与 198.18/19 TUN。
    static java.util.List<String> activeSubnetPrefixes() {
        java.util.List<String> prefixes = new java.util.ArrayList<>();
        try {
            Enumeration<NetworkInterface> interfaces =
                    NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface network = interfaces.nextElement();
                if (!network.isUp() || network.isLoopback()) {
                    continue;
                }
                Enumeration<InetAddress> addresses = network.getInetAddresses();
                while (addresses.hasMoreElements()) {
                    InetAddress address = addresses.nextElement();
                    String text = address.getHostAddress();
                    if (address.isSiteLocalAddress() && text != null && text.indexOf('.') > 0
                            && TargetDeviceManager.isAddressCandidateSafe(text)) {
                        int lastDot = text.lastIndexOf('.');
                        if (lastDot > 0) {
                            String prefix = text.substring(0, lastDot + 1);
                            if (!prefixes.contains(prefix)) {
                                prefixes.add(prefix);
                            }
                        }
                    }
                }
            }
        } catch (Exception ignored) {
            // 没有可用网络信息时由调用方退避。
        }
        return prefixes;
    }

    /// 将形如 "192.168.0." 的前缀解析为前 3 个字节；格式不符返回 null。
    private static byte[] parsePrefixBytes(String prefix) {
        if (prefix == null) {
            return null;
        }
        String[] parts = prefix.split("\\.");
        if (parts.length < 3) {
            return null;
        }
        try {
            int b0 = Integer.parseInt(parts[0]);
            int b1 = Integer.parseInt(parts[1]);
            int b2 = Integer.parseInt(parts[2]);
            if ((b0 | b1 | b2) < 0 || b0 > 255 || b1 > 255 || b2 > 255) {
                return null;
            }
            return new byte[]{(byte) b0, (byte) b1, (byte) b2};
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    /// 随身 Wi-Fi（MiFi）、手机热点与部分路由器会丢弃 UDP 广播（.255）与 mDNS 组播，但允许单播。
    /// 在同一 socket 上对当前 /24 子网快速发射 18 字节单播探测（无 DNS 解析，总流量 ~4.5 KB），
    /// 优先探测路由器最常分配的 DHCP 区间（100..150、2..99、151..254）。
    private static void sendUnicastSubnetSweep(DatagramSocket socket, byte[] payload, String prefix) {
        byte[] prefixBytes = parsePrefixBytes(prefix);
        if (prefixBytes == null) {
            return;
        }
        int[] ranges = {100, 150, 2, 99, 151, 254};
        for (int r = 0; r < ranges.length; r += 2) {
            for (int host = ranges[r]; host <= ranges[r + 1]; host++) {
                try {
                    InetAddress target = InetAddress.getByAddress(
                            new byte[]{prefixBytes[0], prefixBytes[1], prefixBytes[2], (byte) host});
                    socket.send(new DatagramPacket(payload, payload.length, target, DISCOVERY_PORT));
                } catch (Exception ignored) {
                    // 单台不可达不影响其余主机探测。
                }
            }
        }
    }

    /// 广播一次发现并收集接收窗口内的应答，按 computerId 去重；mDNS（NSD）并行，只补 UDP 没找到的电脑。
    static Map<String, DiscoveredComputer> discover(Context context, int timeoutMs) {
        Map<String, DiscoveredComputer> found = new ConcurrentHashMap<>();
        Map<String, DiscoveredComputer> mdnsFound = new ConcurrentHashMap<>();
        java.util.List<String> prefixes = activeSubnetPrefixes();
        Thread mdnsThread = new Thread(
                () -> mdnsFound.putAll(discoverMdns(context, timeoutMs)),
                "PhoneDeckMdnsDiscovery");
        mdnsThread.start();
        WifiManager wifiManager = context.getApplicationContext()
                .getSystemService(WifiManager.class);
        WifiManager.MulticastLock lock = wifiManager == null ? null
                : wifiManager.createMulticastLock("PhoneDeckDiscovery");
        if (lock != null) {
            lock.setReferenceCounted(false);
            lock.acquire();
        }
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            byte[] magic = MAGIC.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            sendTo(socket, magic, "255.255.255.255");
            for (String prefix : prefixes) {
                sendTo(socket, magic, prefix + "255");
            }
            long startedAt = android.os.SystemClock.elapsedRealtime();
            long deadline = startedAt + timeoutMs;
            // 逐台单播扫描只在广播窗口内没有任何应答时才发：每次约 250 个单播会让手机为每个
            // 未知地址发 ARP 广播，Wi-Fi 上广播按最低速率发送，多电脑供音时会加重断续；
            // 普通路由器广播可达，不需要扫描。随身 Wi-Fi / 热点丢弃广播时仍会走到扫描。
            long sweepAt = startedAt + Math.min(SWEEP_AFTER_SILENT_MS, timeoutMs / 2);
            boolean sweepDecided = prefixes.isEmpty();
            byte[] buffer = new byte[MAX_PACKET_BYTES];
            while (true) {
                long now = android.os.SystemClock.elapsedRealtime();
                if (!sweepDecided && now >= sweepAt) {
                    sweepDecided = true;
                    if (found.isEmpty()) {
                        for (String prefix : prefixes) {
                            sendUnicastSubnetSweep(socket, magic, prefix);
                        }
                    }
                }
                long waitUntil = sweepDecided ? deadline : Math.min(deadline, sweepAt);
                if (now >= waitUntil) {
                    if (sweepDecided) {
                        break;
                    }
                    continue;
                }
                socket.setSoTimeout((int) Math.max(1, waitUntil - now));
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                try {
                    socket.receive(packet);
                } catch (SocketTimeoutException timeout) {
                    if (sweepDecided) {
                        break;
                    }
                    continue;
                }
                parseResponse(packet, found);
            }
        } catch (Exception ignored) {
            // 发现失败不阻塞常规探测路径。
        } finally {
            if (lock != null && lock.isHeld()) {
                lock.release();
            }
        }
        try {
            mdnsThread.join(Math.max(200, timeoutMs));
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
        // UDP 应答的源地址就是手机实际到达这台电脑的地址，优先采用。mDNS 的 A 记录可能是
        // Hyper-V / WSL / VMware / VPN 等虚拟网卡地址（Android 12 NSD 只给一个），
        // 原先 putAll 会用它覆盖正确的 UDP 地址，电脑换 IP 后就一直找不回来。
        // 只有 UDP 地址不在手机网段、而 mDNS 地址在时才改用 mDNS（跨旧网段的陈旧应答）。
        for (Map.Entry<String, DiscoveredComputer> entry : mdnsFound.entrySet()) {
            DiscoveredComputer candidate = entry.getValue();
            found.compute(entry.getKey(), (id, existing) -> {
                if (existing == null) {
                    return candidate;
                }
                boolean existingSameSubnet = matchesAnyPrefix(existing.hostAddress, prefixes);
                boolean candidateSameSubnet = matchesAnyPrefix(candidate.hostAddress, prefixes);
                return !existingSameSubnet && candidateSameSubnet ? candidate : existing;
            });
        }
        return Collections.unmodifiableMap(new HashMap<>(found));
    }

    private static boolean matchesAnyPrefix(String address, java.util.List<String> prefixes) {
        if (address == null || prefixes == null) {
            return false;
        }
        for (String prefix : prefixes) {
            if (address.startsWith(prefix)) {
                return true;
            }
        }
        return false;
    }

    /// NSD（mDNS）并行发现：解析 _phonedeck._tcp 的 TXT（computerId 等）与主机/端口。
    /// 系统 NSD 不可用或服务类型缺失时安静返回空表（UDP 仍是主路径）。
    static Map<String, DiscoveredComputer> discoverMdns(Context context, int timeoutMs) {
        Map<String, DiscoveredComputer> found = new HashMap<>();
        NsdManager nsd = context == null ? null
                : context.getApplicationContext().getSystemService(NsdManager.class);
        if (nsd == null) {
            return found;
        }
        CountDownLatch stopped = new CountDownLatch(1);
        NsdManager.DiscoveryListener listener = new NsdManager.DiscoveryListener() {
            @Override
            public void onStartDiscoveryFailed(String serviceType, int errorCode) {
                stopped.countDown();
            }

            @Override
            public void onStopDiscoveryFailed(String serviceType, int errorCode) {
                stopped.countDown();
            }

            @Override
            public void onDiscoveryStarted(String serviceType) {
            }

            @Override
            public void onDiscoveryStopped(String serviceType) {
                stopped.countDown();
            }

            @Override
            public void onServiceFound(NsdServiceInfo serviceInfo) {
                try {
                    nsd.resolveService(serviceInfo, new NsdManager.ResolveListener() {
                        @Override
                        public void onResolveFailed(NsdServiceInfo info, int errorCode) {
                            // 单个服务解析失败不影响其余候选。
                        }

                        @Override
                        public void onServiceResolved(NsdServiceInfo info) {
                            registerResolved(info, found);
                        }
                    });
                } catch (IllegalArgumentException ignored) {
                    // 解析请求参数异常时跳过该服务。
                }
            }

            @Override
            public void onServiceLost(NsdServiceInfo serviceInfo) {
            }
        };
        try {
            nsd.discoverServices(NSD_SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener);
            // 在预算内等待候选：发现窗口用剩余超时，随后显式停止。
            boolean elapsed = !stopped.await(Math.max(300, timeoutMs), TimeUnit.MILLISECONDS);
            stopQuietly(nsd, listener);
            if (elapsed) {
                // 给进行中的 resolve 回调一点收尾时间（不阻塞主路径太久）。
                Thread.sleep(200);
            }
        } catch (Exception ignored) {
            // mDNS 失败不阻塞常规探测路径（UDP 回退）。
            stopQuietly(nsd, listener);
        }
        return found;
    }

    private static void stopQuietly(NsdManager nsd, NsdManager.DiscoveryListener listener) {
        try {
            nsd.stopServiceDiscovery(listener);
        } catch (Exception ignored) {
            // 已停止或未启动均忽略。
        }
    }

    /// NSD 属性值（ASCII 字节）解码为字符串；null/空返回 null。纯函数供单测。
    static String decodeAttribute(byte[] value) {
        if (value == null || value.length == 0) {
            return null;
        }
        // 电脑名可能是中文（如“往里走的MacBook Air”），TXT 值按 UTF-8 解码；
        // Windows mDNS 线格式会对非 ASCII 字节做 %XX 编码，此处兼容还原。
        String text = new String(value, java.nio.charset.StandardCharsets.UTF_8).trim();
        if (text.indexOf('%') >= 0) {
            try {
                text = java.net.URLDecoder.decode(
                        text.replace("+", "%2B"),
                        java.nio.charset.StandardCharsets.UTF_8.name()).trim();
            } catch (Exception ignored) {
                // 非法百分号序列保留原样。
            }
        }
        return text.isEmpty() ? null : text;
    }

    private static void registerResolved(NsdServiceInfo info,
                                         Map<String, DiscoveredComputer> found) {
        try {
            // API 33 起移除 getTxtRecord()，统一走 getAttributes()。
            Map<String, byte[]> attributes = info.getAttributes();
            String computerId = decodeAttribute(attributes.get("computerId"));
            InetAddress host = info.getHost();
            if (computerId == null || host == null) {
                return;
            }
            String address = host.getHostAddress();
            if (address == null || address.isBlank()
                    || !TargetDeviceManager.isAddressCandidateSafe(address)
                    || !isOnLocalIpv4Subnet(host)) {
                return;
            }
            String displayName = decodeAttribute(attributes.get("displayName"));
            found.putIfAbsent(computerId, new DiscoveredComputer(
                    computerId, displayName == null ? computerId : displayName,
                    address, info.getPort(), decodeAttribute(attributes.get("platform"))));
        } catch (Exception ignored) {
            // 单条候选解析失败忽略。
        }
    }

    /// mDNS 只在本链路传播，真正能连上的地址应与手机的某个 IPv4 网段相同；不同网段的
    /// 通常是电脑虚拟网卡地址。手机网段读不到或地址不是 IPv4 时不过滤（交给探测判断）。
    static boolean isOnLocalIpv4Subnet(InetAddress host) {
        if (!(host instanceof java.net.Inet4Address)) {
            return true;
        }
        boolean sawLocalIpv4 = false;
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface network = interfaces.nextElement();
                if (!network.isUp() || network.isLoopback()) {
                    continue;
                }
                for (java.net.InterfaceAddress local : network.getInterfaceAddresses()) {
                    InetAddress localAddress = local.getAddress();
                    if (!(localAddress instanceof java.net.Inet4Address)) {
                        continue;
                    }
                    sawLocalIpv4 = true;
                    if (sameIpv4Subnet(localAddress.getAddress(), host.getAddress(),
                            local.getNetworkPrefixLength())) {
                        return true;
                    }
                }
            }
        } catch (Exception ignored) {
            return true;
        }
        return !sawLocalIpv4;
    }

    /// 纯函数供单测：两个 IPv4 地址在给定前缀长度下是否同网段；前缀无效时视为同网段。
    static boolean sameIpv4Subnet(byte[] left, byte[] right, int prefixLength) {
        if (left == null || right == null || left.length != 4 || right.length != 4) {
            return false;
        }
        if (prefixLength <= 0 || prefixLength > 32) {
            return true;
        }
        int a = ((left[0] & 0xff) << 24) | ((left[1] & 0xff) << 16)
                | ((left[2] & 0xff) << 8) | (left[3] & 0xff);
        int b = ((right[0] & 0xff) << 24) | ((right[1] & 0xff) << 16)
                | ((right[2] & 0xff) << 8) | (right[3] & 0xff);
        int mask = prefixLength == 32 ? -1 : ~(-1 >>> prefixLength);
        return (a & mask) == (b & mask);
    }

    /// 手动地址：直接向这台主机的 UDP 8767 问身份（跨路由/子网时广播发现不到，单播仍可达）。
    static DiscoveredComputer queryHost(String host, int timeoutMs) {
        Map<String, DiscoveredComputer> found = new ConcurrentHashMap<>();
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setSoTimeout(Math.max(200, timeoutMs));
            byte[] magic = MAGIC.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            for (int attempt = 0; attempt < 2 && found.isEmpty(); attempt++) {
                sendTo(socket, magic, host);
                byte[] buffer = new byte[MAX_PACKET_BYTES];
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                try {
                    socket.receive(packet);
                    parseResponse(packet, found);
                } catch (SocketTimeoutException timeout) {
                    // 再试一次。
                }
            }
        } catch (Exception ignored) {
            // 返回 null，由调用方提示。
        }
        return found.isEmpty() ? null : found.values().iterator().next();
    }

    private static void sendTo(DatagramSocket socket, byte[] payload, String host) {
        try {
            socket.send(new DatagramPacket(
                    payload, payload.length,
                    InetAddress.getByName(host), DISCOVERY_PORT));
        } catch (Exception ignored) {
            // 某些网络禁发广播；另一目标仍可能可达。
        }
    }

    private static void parseResponse(
            DatagramPacket packet,
            Map<String, DiscoveredComputer> found) {
        try {
            String text = new String(
                    packet.getData(), 0, packet.getLength(),
                    java.nio.charset.StandardCharsets.UTF_8);
            JSONObject body = new JSONObject(text);
            if (!body.optBoolean("ok", false)
                    || !"phonedeck".equals(body.optString("service", ""))) {
                return;
            }
            String computerId = body.optString("computerId", "").trim();
            if (computerId.isEmpty()) {
                return;
            }
            found.putIfAbsent(computerId, new DiscoveredComputer(
                    computerId,
                    body.optString("displayName", "未命名电脑"),
                    packet.getAddress().getHostAddress(),
                    body.optInt("port", 0),
                    body.optString("platform", "unknown")));
        } catch (Exception ignored) {
            // 非 PhoneDeck 或损坏的应答直接忽略。
        }
    }
}
