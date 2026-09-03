package com.codex.phonedeck;

import android.content.Context;
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

/**
 * 局域网 UDP 发现客户端：广播魔术串到 8767，接收已配对接收端的单播应答。
 * 应答只携带 computerId、名称、端口与能力；建立控制连接仍必须走
 * HTTPS 8766 的令牌与证书固定校验，本类不触碰任何配对密钥。
 */
final class LanDiscoveryClient {
    static final int DISCOVERY_PORT = 8767;
    private static final String MAGIC = "PHONEDECK-DISCOVER";
    private static final int MAX_PACKET_BYTES = 2048;

    static final class DiscoveredComputer {
        final String computerId;
        final String displayName;
        final String hostAddress;
        final int port;

        DiscoveredComputer(String computerId, String displayName,
                           String hostAddress, int port) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.hostAddress = hostAddress;
            this.port = port;
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

    /// 广播一次发现并收集接收窗口内的应答，按 computerId 去重。
    static Map<String, DiscoveredComputer> discover(Context context, int timeoutMs) {
        Map<String, DiscoveredComputer> found = new ConcurrentHashMap<>();
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
        return Collections.unmodifiableMap(new HashMap<>(found));
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
                    body.optInt("port", 0)));
        } catch (Exception ignored) {
            // 非 PhoneDeck 或损坏的应答直接忽略。
        }
    }
}
