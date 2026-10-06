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
        /// M1-B/DEV-03：共享供音组是用户显式选择的集合；新增配对默认不入组，
        /// 存量已配对设备在首次迁移时默认入组（不破坏升级前的共享使用）。
        final boolean sharedGroup;
        /// 用户在手机上自定义了名称：电脑上报的名称不再覆盖它。
        final boolean nameLocked;

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt, List<String> lanAddresses,
               int lanPort, String lanToken, String certificateSha256,
               String lastGoodAddress) {
            this(computerId, displayName, platform, slot, lastSeenAt, lanAddresses,
                    lanPort, lanToken, null, false, certificateSha256, lastGoodAddress);
        }

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt, List<String> lanAddresses,
               int lanPort, String lanToken, String clientId,
               String certificateSha256, String lastGoodAddress) {
            this(computerId, displayName, platform, slot, lastSeenAt, lanAddresses,
                    lanPort, lanToken, clientId, false, certificateSha256, lastGoodAddress);
        }

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt, List<String> lanAddresses,
               int lanPort, String lanToken, String clientId, boolean sharedGroup,
               String certificateSha256, String lastGoodAddress) {
            this(computerId, displayName, platform, slot, lastSeenAt, lanAddresses, lanPort,
                    lanToken, clientId, sharedGroup, certificateSha256, lastGoodAddress, false);
        }

        Device(String computerId, String displayName, String platform,
               int slot, long lastSeenAt, List<String> lanAddresses,
               int lanPort, String lanToken, String clientId, boolean sharedGroup,
               String certificateSha256, String lastGoodAddress, boolean nameLocked) {
            this.nameLocked = nameLocked;
            this.computerId = computerId;
            this.displayName = displayName;
            this.platform = platform;
            this.slot = slot;
            this.lastSeenAt = lastSeenAt;
            this.lanAddresses = new ArrayList<>(lanAddresses);
            this.lanPort = lanPort;
            this.lanToken = lanToken;
            this.clientId = clientId == null || clientId.isBlank() ? null : clientId.trim();
            this.sharedGroup = sharedGroup;
            this.certificateSha256 = certificateSha256;
            this.lastGoodAddress = lastGoodAddress == null || lastGoodAddress.isBlank()
                    ? null : lastGoodAddress.trim();
        }

        Device withAddresses(List<String> addresses, String lastGood) {
            return new Device(computerId, displayName, platform, slot, lastSeenAt, addresses,
                    lanPort, lanToken, clientId, sharedGroup, certificateSha256, lastGood, nameLocked);
        }

        Device withSharedGroup(boolean inGroup) {
            return new Device(computerId, displayName, platform, slot, lastSeenAt, lanAddresses,
                    lanPort, lanToken, clientId, inGroup, certificateSha256, lastGoodAddress, nameLocked);
        }

        Device withName(String name, boolean locked) {
            return new Device(computerId, name, platform, slot, lastSeenAt, lanAddresses,
                    lanPort, lanToken, clientId, sharedGroup, certificateSha256, lastGoodAddress, locked);
        }

        Device withSlot(int newSlot) {
            return new Device(computerId, displayName, platform, newSlot, lastSeenAt, lanAddresses,
                    lanPort, lanToken, clientId, sharedGroup, certificateSha256, lastGoodAddress, nameLocked);
        }

        Device withLastSeen(long seenAt) {
            return new Device(computerId, displayName, platform, slot, seenAt, lanAddresses,
                    lanPort, lanToken, clientId, sharedGroup, certificateSha256, lastGoodAddress, nameLocked);
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
    /// 第一轮正式容量 5 台，10 台作为压力探索（规格 0.6）。满员时拒绝新增，绝不悄悄删除已配对电脑。
    static final int MAX_DEVICES = 10;
    /// 每台电脑最多保留的候选地址；DHCP 变化只替换最旧的历史地址。
    static final int MAX_ADDRESSES = 6;
    /// 探测成功后刷新 lastSeenAt 的最小间隔，避免每 2 秒加密写一次存储。
    private static final long SEEN_PERSIST_INTERVAL_MS = 10 * 60_000L;

    private static TargetDeviceManager instance;

    private final SharedPreferences preferences;
    private final ArrayList<Device> devices = new ArrayList<>();
    private String activeComputerId;
    /// uiPreview 验收通道（包名 .preview）允许把 adb 反向的回环地址存为配对地址，
    /// 供网络受限环境的自动化协议验收；生产包保持严格地址过滤。
    private final boolean previewChannel;

    /// 进程内唯一实例：主界面、共享麦克风服务和各设置页共用同一份设备状态，
    /// 不再各自整表写回而互相覆盖，也不必定时从 Keystore 重新解密。
    static synchronized TargetDeviceManager get(Context context) {
        if (instance == null) {
            instance = new TargetDeviceManager(context.getApplicationContext());
        }
        return instance;
    }

    private TargetDeviceManager(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(
                PREFS_NAME, Context.MODE_PRIVATE);
        previewChannel = context.getPackageName().endsWith(".preview");
        load();
    }

    /// 新电脑能否加入：已存在或尚未满员。
    synchronized boolean canAdd(String computerId) {
        return find(computerId) != null || devices.size() < MAX_DEVICES;
    }

    /// 保留 last-good，其余按新近程度保留，最多 max 个（输入按从旧到新排列）。
    static List<String> capAddresses(List<String> addresses, String lastGood, int max) {
        ArrayList<String> result = new ArrayList<>(addresses);
        int index = 0;
        while (result.size() > max && index < result.size()) {
            if (result.get(index).equals(lastGood)) {
                index++;
            } else {
                result.remove(index);
            }
        }
        return result;
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
        if (existing == null && devices.size() >= MAX_DEVICES) {
            // 满员：拒绝新增，由调用方提示用户先删除一台。
            return null;
        }
        int slot = existing == null ? nextSlot() : existing.slot;
        boolean locked = existing != null && existing.nameLocked;
        Device updated = new Device(
                computerId.trim(), locked ? existing.displayName : safeName, safePlatform, slot,
                System.currentTimeMillis(),
                existing == null ? java.util.Collections.emptyList() : existing.lanAddresses,
                existing == null ? 0 : existing.lanPort,
                existing == null ? null : existing.lanToken,
                existing == null ? null : existing.clientId,
                existing != null && existing.sharedGroup,
                existing == null ? null : existing.certificateSha256,
                existing == null ? null : existing.lastGoodAddress,
                locked);
        if (existing != null) {
            devices.remove(existing);
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
        // USB 只签发电脑的共享令牌：保存时不能再带旧的逐手机 clientId（否则 Bearer 组合无效、
        // 电脑一直回 401，显示“配对已失效”）。之后可经升级提示换领新的独立凭据。
        String token = accessToken.trim();
        String clientId = null;
        Device paired = new Device(
                base.computerId, base.displayName, base.platform,
                base.slot, System.currentTimeMillis(), safeAddresses, port,
                token, clientId, base.sharedGroup,
                certificateSha256.trim().toLowerCase(), lastGood, base.nameLocked);
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
        long now = System.currentTimeMillis();
        boolean seenStale = now - device.lastSeenAt >= SEEN_PERSIST_INTERVAL_MS;
        if (trimmed.equals(device.lastGoodAddress) && !seenStale) {
            return false;
        }
        // 探测成功即“最近在线”：按节流间隔刷新，供列表排序与诊断使用。
        Device updated = device.withAddresses(device.lanAddresses, trimmed)
                .withLastSeen(seenStale ? now : device.lastSeenAt);
        devices.remove(device);
        devices.add(updated);
        save();
        return !trimmed.equals(device.lastGoodAddress);
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
        Device updated = device.withAddresses(
                capAddresses(merged, device.lastGoodAddress, MAX_ADDRESSES),
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

    /// M1-B/DEV-03：显式共享组切换；下一轮探测循环即生效（移除即停发该目标流）。
    synchronized boolean setSharedGroup(String computerId, boolean inGroup) {
        Device device = find(computerId);
        if (device == null || device.sharedGroup == inGroup) {
            return device != null;
        }
        devices.remove(device);
        devices.add(device.withSharedGroup(inGroup));
        save();
        return true;
    }

    /// 手机本地重命名；空名称恢复为电脑上报的名称（下次连接时更新）。
    synchronized boolean rename(String computerId, String name) {
        Device device = find(computerId);
        if (device == null) {
            return false;
        }
        String trimmed = name == null ? "" : name.trim();
        if (trimmed.length() > 40) {
            trimmed = trimmed.substring(0, 40);
        }
        devices.remove(device);
        devices.add(trimmed.isEmpty()
                ? device.withName(device.displayName, false)
                : device.withName(trimmed, true));
        save();
        return true;
    }

    /// 在编号顺序中上移（delta<0）或下移一位：与相邻电脑交换编号。
    synchronized boolean move(String computerId, int delta) {
        List<Device> ordered = list();
        int index = -1;
        for (int i = 0; i < ordered.size(); i++) {
            if (ordered.get(i).computerId.equalsIgnoreCase(computerId)) {
                index = i;
                break;
            }
        }
        int target = index + Integer.signum(delta);
        if (index < 0 || delta == 0 || target < 0 || target >= ordered.size()) {
            return false;
        }
        Device first = ordered.get(index);
        Device second = ordered.get(target);
        devices.remove(first);
        devices.remove(second);
        devices.add(first.withSlot(second.slot));
        devices.add(second.withSlot(first.slot));
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

    /// 从存储重新读取（仅用于存储被外部改写的情况；进程内共享单例不需要定时调用）。
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
                        if (!address.isEmpty() && hostAcceptable(address)
                                && !addresses.contains(address)) {
                            addresses.add(address);
                        }
                    }
                }
                String lastGood = item.optString("lastGoodAddress", null);
                String lanToken = item.optString("lanToken", null);
                String certificateSha256 = item.optString("certificateSha256", null);
                // 迁移：旧记录无 sharedGroup 字段时，已配对设备默认入组（DEV-03 存量兼容）。
                boolean sharedGroup = item.has("sharedGroup")
                        ? item.optBoolean("sharedGroup")
                        : lanToken != null && !lanToken.isBlank()
                                && certificateSha256 != null && !certificateSha256.isBlank()
                                && !addresses.isEmpty() && item.optInt("lanPort", 0) > 0;
                devices.add(new Device(
                        computerId,
                        item.optString("displayName", "未命名电脑"),
                        item.optString("platform", "unknown"),
                        item.optInt("slot", nextSlot()),
                        item.optLong("lastSeenAt", 0L),
                        addresses,
                        item.optInt("lanPort", 0),
                        lanToken,
                        item.optString("clientId", null),
                        sharedGroup,
                        certificateSha256,
                        hostAcceptable(lastGood) ? lastGood : null,
                        item.optBoolean("nameLocked", false)));
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
                item.put("sharedGroup", device.sharedGroup);
                item.put("certificateSha256", device.certificateSha256);
                item.put("lastGoodAddress", device.lastGoodAddress);
                item.put("nameLocked", device.nameLocked);
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

    /// 保存配对（同一 Wi-Fi 免扫码连接）得到的逐手机凭据（Bearer）。
    synchronized Device savePairedComputer(
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
                clientToken.trim(), clientId.trim(), false,
                certificateSha256.trim().toLowerCase(),
                safeAddresses.contains(base.lastGoodAddress) ? base.lastGoodAddress : null,
                base.nameLocked);
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
     * 先用新凭据经 Bearer health 验证（NearbyPairingClient.verifyCredential，
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
            // 并发路径（如重新配对）已写入独立凭据：无需也不应覆盖。
            return CredentialUpgradeResult.success(device);
        }
        String token = clientToken.trim();
        String id = clientId.trim();
        for (String host : upgradeVerifyHosts(device, preferredHost)) {
            if (NearbyPairingClient.verifyCredential(
                    host, device.computerId, device.lanPort,
                    token, id, device.certificateSha256)) {
                return commitCredentialUpgrade(device.computerId, token, id, host);
            }
        }
        return CredentialUpgradeResult.failure("新凭据验证未通过，已保留原共享令牌");
    }

    /// 验证地址顺序：rotate 刚成功的主机 → last-good → 其余候选（去重）。
    /// preview 验收通道沿用 savePairedComputer 的宽松地址规则（允许 adb 反向回环）。
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
                current.lanPort, clientToken, clientId, current.sharedGroup,
                current.certificateSha256,
                current.lanAddresses.contains(verifiedHost)
                        ? verifiedHost : current.lastGoodAddress,
                current.nameLocked);
        devices.remove(current);
        devices.add(upgraded);
        save();
        return CredentialUpgradeResult.success(upgraded);
    }
}
