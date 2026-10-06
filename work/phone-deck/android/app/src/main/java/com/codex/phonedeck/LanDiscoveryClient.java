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
                    if (address.isSiteLocalAddress() && text.indexOf('.') > 0) {
                        int lastDot = text.lastIndexOf('.');
                        if (lastDot > 0) {
                            return text.substring(0, lastDot + 1);
                        }
                    }
                }
            }
        } catch (Exception ignored) {
            // 没有可用网络信息时由调用方退避。
        }
        return null;
    }

    /// 广播一次发现并收集接收窗口内的应答，按 computerId 去重；mDNS（NSD）并行并入。
    static Map<String, DiscoveredComputer> discover(Context context, int timeoutMs) {
        Map<String, DiscoveredComputer> found = new ConcurrentHashMap<>();
        Thread mdnsThread = new Thread(
                () -> found.putAll(discoverMdns(context, timeoutMs)),
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
            socket.setSoTimeout(Math.max(200, timeoutMs));
            byte[] magic = MAGIC.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            sendTo(socket, magic, "255.255.255.255");
            String prefix = currentSubnetPrefix();
            if (prefix != null) {
                sendTo(socket, magic, prefix + "255");
            }
            long deadline = android.os.SystemClock.elapsedRealtime() + timeoutMs;
            byte[] buffer = new byte[MAX_PACKET_BYTES];
            while (android.os.SystemClock.elapsedRealtime() < deadline) {
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                try {
                    socket.receive(packet);
                } catch (SocketTimeoutException timeout) {
                    break;
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
        return Collections.unmodifiableMap(new HashMap<>(found));
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
        // 电脑名可能是中文（如“往里走的MacBook Air”），TXT 值按 UTF-8 解码。
        String text = new String(value, java.nio.charset.StandardCharsets.UTF_8).trim();
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
                    || !TargetDeviceManager.isAddressCandidateSafe(address)) {
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
