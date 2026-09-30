package com.codex.phonedeck;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

final class TargetDeviceManager {
    static final class Device {
        final String computerId;
        final String displayName;
        final String platform;
        final int slot;
        final long lastSeenAt;
        final List<String> lanAddresses;
        final int lanPort;
        final String lanToken;
        final String clientId;
        final String certificateSha256;
        final String lastGoodAddress;

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt, List<String> lanAddresses,
               int lanPort, String lanToken, String certificateSha256,
               String lastGoodAddress) {
            this(computerId, displayName, platform, slot, lastSeenAt, lanAddresses,
                    lanPort, lanToken, null, certificateSha256, lastGoodAddress);
        }

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt, List<String> lanAddresses,
               int lanPort, String lanToken, String clientId,
               String certificateSha256, String lastGoodAddress) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.platform = platform;
            this.slot = slot;
            this.lastSeenAt = lastSeenAt;
            this.lanAddresses = new ArrayList<>(lanAddresses);
            this.lanPort = lanPort;
            this.lanToken = lanToken;
            this.clientId = clientId == null || clientId.isBlank() ? null : clientId.trim();
            this.certificateSha256 = certificateSha256;
            this.lastGoodAddress = lastGoodAddress == null || lastGoodAddress.isBlank()
                    ? null : lastGoodAddress.trim();
        }

        boolean hasLanPairing() {
            return !lanAddresses.isEmpty() && lanPort > 0
                    && lanToken != null && !lanToken.isBlank()
                    && certificateSha256 != null && !certificateSha256.isBlank();
        }

        /// M1-A 逐手机凭据（clientId 存在即新式凭据，Bearer 头；否则旧共享令牌）。
        boolean hasClientCredential() {
            return clientId != null && lanToken != null && !lanToken.isBlank();
        }
    }

    private static final String PREFS_NAME = "PhoneDeckDevices";
    private static final String KEY_DEVICES = "known_devices";
    private static final String KEY_DEVICES_ENCRYPTED = "known_devices_enc";
    private static final String KEY_ACTIVE = "active_computer_id";
    private static final int MAX_DEVICES = 8;

    private final SharedPreferences preferences;
    private final ArrayList<Device> devices = new ArrayList<>();
    private String activeComputerId;
    /// uiPreview 验收通道（包名 .preview）允许把 adb 反向的回环地址存为配对地址，
    /// 供网络受限环境的自动化协议验收；生产包保持严格地址过滤。
    private final boolean previewChannel;

    TargetDeviceManager(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(
                PREFS_NAME, Context.MODE_PRIVATE);
        previewChannel = context.getPackageName().endsWith(".preview");
        load();
    }

    /// 稳定身份是 computerId + 证书指纹 + 令牌；IP 只是缓存。
    /// 保存与读取时都过滤回环、APIPA 和 198.18.0.0/15 TUN 网段，
    /// 旧版本已保存的虚拟网卡地址在加载时自动清除（其余配对资料不受影响）。
    static boolean isAddressCandidateSafe(String address) {
        if (address == null) {
            return false;
        }
        String trimmed = address.trim();
        return !trimmed.isEmpty()
                && !trimmed.startsWith("127.")
                && !trimmed.startsWith("169.254.")
                && !trimmed.startsWith("198.18.")
                && !trimmed.startsWith("198.19.");
    }

    synchronized List<Device> list() {
        ArrayList<Device> result = new ArrayList<>(devices);
        result.sort(Comparator.comparingInt(device -> device.slot));
        return result;
    }

    synchronized String getActiveComputerId() {
        return activeComputerId;
    }

    synchronized Device find(String computerId) {
        if (computerId == null) {
            return null;
        }
        for (Device device : devices) {
            if (device.computerId.equalsIgnoreCase(computerId)) {
                return device;
            }
        }
        return null;
    }

    synchronized Device upsert(String computerId, String displayName, String platform) {
        if (computerId == null || computerId.isBlank()) {
            return null;
        }
        String safeName = displayName == null || displayName.isBlank()
                ? "未命名电脑" : displayName.trim();
        String safePlatform = platform == null || platform.isBlank()
                ? "unknown" : platform.trim();
        Device existing = find(computerId);
        int slot = existing == null ? nextSlot() : existing.slot;
        Device updated = new Device(
                computerId.trim(), safeName, safePlatform, slot,
                System.currentTimeMillis(),
                existing == null ? java.util.Collections.emptyList() : existing.lanAddresses,
                existing == null ? 0 : existing.lanPort,
                existing == null ? null : existing.lanToken,
                existing == null ? null : existing.certificateSha256,
                existing == null ? null : existing.lastGoodAddress);
        if (existing != null) {
            devices.remove(existing);
        } else if (devices.size() >= MAX_DEVICES) {
            Device oldest = devices.stream()
                    .min(Comparator.comparingLong(device -> device.lastSeenAt))
                    .orElse(null);
            if (oldest != null && !oldest.computerId.equalsIgnoreCase(activeComputerId)) {
                devices.remove(oldest);
            } else {
                return existing;
            }
        }
        devices.add(updated);
        if (activeComputerId == null || activeComputerId.isBlank()) {
            activeComputerId = updated.computerId;
        }
        save();
        return updated;
    }

    synchronized Device saveLanPairing(
            String computerId,
            String displayName,
            String platform,
            List<String> addresses,
            int port,
            String accessToken,
            String certificateSha256) {
        Device base = upsert(computerId, displayName, platform);
        if (base == null || addresses == null || addresses.isEmpty()
                || port < 1 || port > 65535
                || accessToken == null || accessToken.isBlank()
                || certificateSha256 == null || certificateSha256.isBlank()) {
            return null;
        }
        ArrayList<String> safeAddresses = new ArrayList<>();
        for (String address : addresses) {
            if (!isAddressCandidateSafe(address)) {
                continue;
            }
            String trimmed = address.trim();
            if (trimmed.length() <= 255 && !safeAddresses.contains(trimmed)) {
                safeAddresses.add(trimmed);
            }
        }
        if (safeAddresses.isEmpty()) {
            return null;
        }
        String lastGood = base.lastGoodAddress != null
                && safeAddresses.contains(base.lastGoodAddress)
                ? base.lastGoodAddress : null;
        Device paired = new Device(
                base.computerId, base.displayName, base.platform,
                base.slot, System.currentTimeMillis(), safeAddresses, port,
                accessToken.trim(), certificateSha256.trim().toLowerCase(), lastGood);
        devices.remove(base);
        devices.add(paired);
        save();
        return paired;
    }

    /// 探测成功后记录最近可用地址；地址只是缓存，配对身份不变。
    synchronized boolean recordLastGoodAddress(String computerId, String address) {
        Device device = find(computerId);
        if (device == null || !isAddressCandidateSafe(address)) {
            return false;
        }
        String trimmed = address.trim();
        if (trimmed.equals(device.lastGoodAddress)) {
            return false;
        }
        Device updated = new Device(
                device.computerId, device.displayName, device.platform,
                device.slot, device.lastSeenAt, device.lanAddresses,
                device.lanPort, device.lanToken, device.certificateSha256, trimmed);
        devices.remove(device);
        devices.add(updated);
        save();
        return true;
    }

    /// 合并自动发现得到的新地址（仍需 HTTPS + 令牌 + 证书固定验证后才可用）。
    synchronized boolean mergeDiscoveredAddress(String computerId, String address) {
        Device device = find(computerId);
        if (device == null || !isAddressCandidateSafe(address)) {
            return false;
        }
        String trimmed = address.trim();
        if (device.lanAddresses.contains(trimmed)) {
            return false;
        }
        ArrayList<String> merged = new ArrayList<>(device.lanAddresses);
        merged.add(trimmed);
        Device updated = new Device(
                device.computerId, device.displayName, device.platform,
                device.slot, device.lastSeenAt, merged,
                device.lanPort, device.lanToken, device.certificateSha256,
                device.lastGoodAddress);
        devices.remove(device);
        devices.add(updated);
        save();
        return true;
    }

    synchronized boolean select(String computerId) {
        Device device = find(computerId);
        if (device == null) {
            return false;
        }
        activeComputerId = device.computerId;
        save();
        return true;
    }

    /// 删除一台已配对电脑（目标切换器长按触发）。删除当前目标时
    /// 自动切到剩余列表的第一台；被 USB 重新发现的电脑会再次自动配对。
    synchronized boolean remove(String computerId) {
        Device device = find(computerId);
        if (device == null) {
            return false;
        }
        devices.remove(device);
        if (device.computerId.equalsIgnoreCase(activeComputerId)) {
            activeComputerId = devices.isEmpty() ? null : devices.get(0).computerId;
        }
        save();
        return true;
    }

    /// 主界面与共享麦克风服务各持有一份实例；任一方更新配对或删除设备后，
    /// 另一方在下一轮探测前调用 reload() 刷新自己的视图。
    synchronized void reload() {
        devices.clear();
        load();
    }

    private int nextSlot() {
        for (int slot = 1; slot <= MAX_DEVICES; slot++) {
            final int candidate = slot;
            if (devices.stream().noneMatch(device -> device.slot == candidate)) {
                return slot;
            }
        }
        return MAX_DEVICES;
    }

    private void load() {
        activeComputerId = preferences.getString(KEY_ACTIVE, null);
        String raw = preferences.getString(KEY_DEVICES_ENCRYPTED, null);
        boolean encrypted = raw != null;
        if (!encrypted) {
            raw = preferences.getString(KEY_DEVICES, "[]");
        }
        if (encrypted) {
            try {
                raw = new String(KeystoreCipher.decrypt(
                        java.util.Base64.getDecoder().decode(raw)),
                        java.nio.charset.StandardCharsets.UTF_8);
            } catch (Exception exception) {
                // 解密失败（如系统还原后 Keystore 密钥丢失）：视为无配对，
                // 下次 save() 会以当前 Keystore 重新加密落盘。
                android.util.Log.w("PhoneDeckDevices", "加密存储解密失败，重置配对", exception);
                raw = "[]";
            }
        }
        try {
            JSONArray array = new JSONArray(raw);
            for (int index = 0; index < array.length() && devices.size() < MAX_DEVICES; index++) {
                JSONObject item = array.getJSONObject(index);
                String computerId = item.optString("computerId", "").trim();
                if (computerId.isEmpty() || find(computerId) != null) {
                    continue;
                }
                ArrayList<String> addresses = new ArrayList<>();
                JSONArray addressArray = item.optJSONArray("lanAddresses");
                if (addressArray != null) {
                    for (int addressIndex = 0;
                         addressIndex < addressArray.length(); addressIndex++) {
                        String address = addressArray.optString(addressIndex, "").trim();
                        if (!address.isEmpty() && isAddressCandidateSafe(address)
                                && !addresses.contains(address)) {
                            addresses.add(address);
                        }
                    }
                }
                String lastGood = item.optString("lastGoodAddress", null);
                devices.add(new Device(
                        computerId,
                        item.optString("displayName", "未命名电脑"),
                        item.optString("platform", "unknown"),
                        item.optInt("slot", nextSlot()),
                        item.optLong("lastSeenAt", 0L),
                        addresses,
                        item.optInt("lanPort", 0),
                        item.optString("lanToken", null),
                        item.optString("clientId", null),
                        item.optString("certificateSha256", null),
                        isAddressCandidateSafe(lastGood) ? lastGood : null));
            }
        } catch (Exception ignored) {
            devices.clear();
            activeComputerId = null;
            save();
        }
        if (find(activeComputerId) == null) {
            activeComputerId = devices.isEmpty() ? null : devices.get(0).computerId;
        }
    }

    private void save() {
        JSONArray array = new JSONArray();
        try {
            for (Device device : devices) {
                JSONObject item = new JSONObject();
                item.put("computerId", device.computerId);
                item.put("displayName", device.displayName);
                item.put("platform", device.platform);
                item.put("slot", device.slot);
                item.put("lastSeenAt", device.lastSeenAt);
                item.put("lanAddresses", new JSONArray(device.lanAddresses));
                item.put("lanPort", device.lanPort);
                item.put("lanToken", device.lanToken);
                item.put("clientId", device.clientId == null ? JSONObject.NULL : device.clientId);
                item.put("certificateSha256", device.certificateSha256);
                item.put("lastGoodAddress", device.lastGoodAddress);
                array.put(item);
            }
        } catch (Exception ignored) {
            return;
        }
        SharedPreferences.Editor editor = preferences.edit()
                .putString(KEY_ACTIVE, activeComputerId);
        // 设计 §7：凭据记录经 AndroidKeyStore AES-GCM 封装后落 SharedPreferences；
        // Keystore 异常时回退旧明文键（可用性优先，降级记录在案）。
        try {
            byte[] sealed = KeystoreCipher.encrypt(
                    array.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));
            editor.putString(KEY_DEVICES_ENCRYPTED,
                    java.util.Base64.getEncoder().encodeToString(sealed))
                    .remove(KEY_DEVICES);
        } catch (Exception exception) {
            android.util.Log.w("PhoneDeckDevices", "Keystore 加密失败，回退明文存储", exception);
            editor.putString(KEY_DEVICES, array.toString())
                    .remove(KEY_DEVICES_ENCRYPTED);
        }
        editor.apply();
    }

    /// M1-A A3：保存扫码配对得到的逐手机凭据（Bearer）。
    synchronized Device saveQrPairing(
            String computerId,
            String displayName,
            String platform,
            List<String> addresses,
            int port,
            String clientToken,
            String clientId,
            String certificateSha256) {
        Device base = upsert(computerId, displayName, platform);
        if (base == null || addresses == null || addresses.isEmpty()
                || port < 1 || port > 65535
                || clientToken == null || clientToken.isBlank()
                || clientId == null || clientId.isBlank()
                || certificateSha256 == null || certificateSha256.isBlank()) {
            return null;
        }
        ArrayList<String> safeAddresses = new ArrayList<>();
        for (String address : addresses) {
            boolean acceptable = previewChannel
                    ? address != null && !address.trim().isEmpty()
                    : isAddressCandidateSafe(address);
            if (acceptable) {
                String trimmed = address.trim();
                if (trimmed.length() <= 255 && !safeAddresses.contains(trimmed)) {
                    safeAddresses.add(trimmed);
                }
            }
        }
        if (safeAddresses.isEmpty()) {
            return null;
        }
        Device paired = new Device(
                base.computerId, base.displayName, base.platform, base.slot,
                System.currentTimeMillis(), safeAddresses, port,
                clientToken.trim(), clientId.trim(),
                certificateSha256.trim().toLowerCase(),
                safeAddresses.contains(base.lastGoodAddress) ? base.lastGoodAddress : null);
        devices.remove(base);
        devices.add(paired);
        save();
        return paired;
    }

    /// M1-A A4 升级保存结果：device 非空=已替换为逐手机凭据；failure 为用户文案。
    static final class CredentialUpgradeResult {
        final Device device;
        final String failure;

        private CredentialUpgradeResult(Device device, String failure) {
            this.device = device;
            this.failure = failure;
        }

        static CredentialUpgradeResult success(Device device) {
            return new CredentialUpgradeResult(device, null);
        }

        static CredentialUpgradeResult failure(String reason) {
            return new CredentialUpgradeResult(null, reason);
        }
    }

    /**
     * M1-A A4：保存 rotate 签发的逐手机凭据（设计 §5.5 回退纪律）。
     * 先用新凭据经 Bearer health 验证（QrPairingClient.verifyCredential，
     * 首选 rotate 刚成功的主机，其次 last-good 与其余候选地址），验证成功
     * 才替换设备记录；失败不动存储，旧共享令牌保持可用并返回失败原因。
     * 验证含网络请求，且不能持有本类锁等网络（会卡 UI 线程的读操作），
     * 必须在后台线程调用；替换本身在锁内重新读取当前记录，不覆盖并发改动。
     */
    CredentialUpgradeResult applyCredentialUpgrade(
            String computerId, String clientToken, String clientId, String preferredHost) {
        if (clientToken == null || clientToken.isBlank()
                || clientId == null || clientId.isBlank()) {
            return CredentialUpgradeResult.failure("升级响应缺少凭据");
        }
        Device device = find(computerId);
        if (device == null) {
            return CredentialUpgradeResult.failure("找不到该电脑的配对记录");
        }
        if (!device.hasLanPairing()) {
            return CredentialUpgradeResult.failure("该电脑没有可用的局域网配对");
        }
        if (device.hasClientCredential()) {
            // 并发路径（如重新扫码配对）已写入独立凭据：无需也不应覆盖。
            return CredentialUpgradeResult.success(device);
        }
        String token = clientToken.trim();
        String id = clientId.trim();
        for (String host : upgradeVerifyHosts(device, preferredHost)) {
            if (QrPairingClient.verifyCredential(
                    host, device.computerId, device.lanPort,
                    token, id, device.certificateSha256)) {
                return commitCredentialUpgrade(device.computerId, token, id, host);
            }
        }
        return CredentialUpgradeResult.failure("新凭据验证未通过，已保留原共享令牌");
    }

    /// 验证地址顺序：rotate 刚成功的主机 → last-good → 其余候选（去重）。
    /// preview 验收通道沿用 saveQrPairing 的宽松地址规则（允许 adb 反向回环）。
    private List<String> upgradeVerifyHosts(Device device, String preferredHost) {
        ArrayList<String> hosts = new ArrayList<>();
        String[] ordered = {preferredHost, device.lastGoodAddress};
        for (String candidate : ordered) {
            if (hostAcceptable(candidate) && !hosts.contains(candidate.trim())) {
                hosts.add(candidate.trim());
            }
        }
        for (String address : device.lanAddresses) {
            if (hostAcceptable(address) && !hosts.contains(address.trim())) {
                hosts.add(address.trim());
            }
        }
        return hosts;
    }

    private boolean hostAcceptable(String host) {
        if (host == null) {
            return false;
        }
        String trimmed = host.trim();
        return previewChannel ? !trimmed.isEmpty() : isAddressCandidateSafe(trimmed);
    }

    /// 验证成功后的落盘替换：锁内重读记录，配对被并发重置/删除时不覆盖。
    private synchronized CredentialUpgradeResult commitCredentialUpgrade(
            String computerId, String clientToken, String clientId, String verifiedHost) {
        Device current = find(computerId);
        if (current == null) {
            return CredentialUpgradeResult.failure("配对记录已删除，升级未保存");
        }
        if (current.hasClientCredential()) {
            return CredentialUpgradeResult.success(current);
        }
        if (!current.hasLanPairing()) {
            return CredentialUpgradeResult.failure("该电脑的配对已被重置，升级未保存");
        }
        Device upgraded = new Device(
                current.computerId, current.displayName, current.platform,
                current.slot, System.currentTimeMillis(), current.lanAddresses,
                current.lanPort, clientToken, clientId, current.certificateSha256,
                current.lanAddresses.contains(verifiedHost)
                        ? verifiedHost : current.lastGoodAddress);
        devices.remove(current);
        devices.add(upgraded);
        save();
        return CredentialUpgradeResult.success(upgraded);
    }
}
