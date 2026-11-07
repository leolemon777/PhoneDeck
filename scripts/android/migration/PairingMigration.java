package com.codex.phonedeck.migration;

import android.os.Process;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.util.Xml;

import org.json.JSONArray;
import org.json.JSONObject;
import org.xmlpull.v1.XmlPullParser;
import org.xmlpull.v1.XmlSerializer;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.StandardOpenOption;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.Security;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * One-shot, app-process-scoped pairing migration helper.
 *
 * <p>This source is intentionally outside the production Android app.  It is
 * compiled into a small jar, staged through {@code run-as}, and invoked with
 * {@code app_process}.  The caller must stop the app before using import or
 * verify.  The helper never exports a Keystore key and never removes one.</p>
 *
 * <pre>
 *   PairingMigration export &lt;/data/user/0/com.codex.phonedeck&gt;
 *   PairingMigration import &lt;/data/user/0/com.codex.phonedeck&gt; --stdin-base64
 *   PairingMigration verify &lt;/data/user/0/com.codex.phonedeck&gt; --stdin-base64
 * </pre>
 *
 * <p>Export writes only a framed UTF-8 JSON payload to stdout.  Import and
 * verify read the same frame from stdin.  With {@code --stdin-base64}, stdin
 * is one strict Base64 line containing the framed UTF-8 payload; this avoids
 * shell/ADB binary and newline transformations.  No diagnostic output ever
 * includes a token, client id, display name, or the JSON payload.</p>
 */
public final class PairingMigration {
    private static final String PACKAGE_NAME = "com.codex.phonedeck";
    private static final String PREFS_FILE = "PhoneDeckDevices.xml";
    private static final String KEY_ACTIVE = "active_computer_id";
    private static final String KEY_DEVICES = "known_devices";
    private static final String KEY_DEVICES_ENCRYPTED = "known_devices_enc";
    private static final String KEY_ALIAS = "phonedeck_device_store";
    private static final String ANDROID_KEYSTORE = "AndroidKeyStore";
    private static final String CIPHER_TRANSFORMATION = "AES/GCM/NoPadding";
    private static final int FORMAT = 1;
    private static final int MAX_DEVICES = 10;
    private static final int MAX_ADDRESSES = 6;
    private static final int MAX_JSON_BYTES = 4 * 1024 * 1024;
    private static final int IV_BYTES = 12;
    private static final int TAG_BITS = 128;

    private static final String FRAME_START = "PHONEDECK_PAIRING_MIGRATION_V1";
    private static final String FRAME_END = "PHONEDECK_PAIRING_MIGRATION_END";

    private static final String[] PAYLOAD_FIELDS = {
            "format", "package", "uid", "activeComputerId", "devices"
    };
    private static final String[] DEVICE_FIELDS = {
            "computerId", "displayName", "platform", "slot", "lastSeenAt",
            "lanAddresses", "lanPort", "lanToken", "clientId",
            "sharedGroup", "certificateSha256", "lastGoodAddress", "nameLocked"
    };

    private PairingMigration() {
    }

    public static void main(String[] args) {
        String mode = normalizeMode(args.length == 0 ? null : args[0]);
        try {
            run(args);
        } catch (MigrationFailure failure) {
            writeError(mode, failure.code);
            System.exit(1);
        } catch (Throwable failure) {
            // Do not print exception messages: JSON parsing and crypto
            // providers can include input fragments in their messages.
            writeError(mode, "unexpected_" + failure.getClass().getSimpleName());
            System.exit(1);
        }
    }

    private static void run(String[] args) throws Exception {
        if (args.length < 2 || args.length > 3) {
            throw failure("usage");
        }
        String mode = args[0];
        if (!"export".equals(mode) && !"import".equals(mode) && !"verify".equals(mode)) {
            throw failure("unknown_mode");
        }
        boolean base64Stdin = args.length == 3 && "--stdin-base64".equals(args[2]);
        if (args.length == 3 && !base64Stdin) {
            throw failure("unknown_option");
        }
        File dataDir = validateDataDir(new File(args[1]));
        if ("export".equals(mode)) {
            if (base64Stdin) {
                throw failure("export_does_not_read_stdin");
            }
            exportPayload(dataDir);
        } else if ("import".equals(mode)) {
            importPayload(dataDir, base64Stdin);
        } else {
            verifyPayload(dataDir, base64Stdin);
        }
    }

    private static void exportPayload(File dataDir) throws Exception {
        File source = preferencesFile(dataDir, false);
        Preferences preferences = readPreferences(source);
        rejectUnsupportedPreferenceRecords(preferences);
        if (preferences.values.containsKey(KEY_DEVICES_ENCRYPTED)
                && preferences.values.containsKey(KEY_DEVICES)) {
            throw failure("ambiguous_encrypted_and_plaintext_records");
        }
        String devicesJson;
        if (preferences.values.containsKey(KEY_DEVICES_ENCRYPTED)) {
            installAndroidKeyStoreProvider();
            SecretKey key = loadExistingKey();
            byte[] sealed = decodeBase64(preferences.values.get(KEY_DEVICES_ENCRYPTED));
            byte[] plain = null;
            try {
                plain = decrypt(key, sealed);
                devicesJson = decodeUtf8(plain);
            } finally {
                clear(sealed);
                clear(plain);
            }
        } else if (preferences.values.containsKey(KEY_DEVICES)) {
            // Read-only compatibility for an old plaintext installation.  No
            // plaintext value is ever written by this helper.
            devicesJson = preferences.values.get(KEY_DEVICES);
        } else {
            throw failure("device_record_missing");
        }

        List<DeviceRecord> devices = parseDevicesJson(devicesJson);
        String activeComputerId = preferences.values.get(KEY_ACTIVE);
        validateActiveComputerId(activeComputerId, devices);
        Payload payload = new Payload(Process.myUid(), activeComputerId, devices);
        String canonical = canonicalPayload(payload);
        byte[] framed = frame(canonical);
        try {
            writeBytes(System.out, framed);
        } finally {
            clear(framed);
        }
        writeSummary("export", payload, sha256(canonical.getBytes(StandardCharsets.UTF_8)),
                false, false, 0);
    }

    private static void importPayload(File dataDir, boolean base64Stdin) throws Exception {
        Payload payload = readPayloadFromStdin(base64Stdin);
        File target = preferencesFile(dataDir, true);
        if (pathExistsNoFollow(target)) {
            throw failure("target_preferences_already_exists");
        }
        File targetBackup = new File(target.getPath() + ".bak");
        if (pathExistsNoFollow(targetBackup)) {
            throw failure("target_preferences_backup_exists");
        }

        installAndroidKeyStoreProvider();
        KeyAccess keyAccess = loadOrCreateKeyOnlyWhenTargetAbsent(target);
        byte[] devicesPlain = null;
        byte[] sealed = null;
        byte[] xml = null;
        File temp = null;
        try {
            devicesPlain = canonicalDevices(payload.devices).getBytes(StandardCharsets.UTF_8);
            sealed = encrypt(keyAccess.key, devicesPlain);
            byte[] roundTrip = null;
            try {
                roundTrip = decrypt(keyAccess.key, sealed);
                if (!constantTimeEquals(devicesPlain, roundTrip)) {
                    throw failure("encrypt_decrypt_roundtrip_mismatch");
                }
            } finally {
                clear(roundTrip);
            }

            xml = preferencesXml(payload.activeComputerId, sealed);
            File sharedPrefsDir = sharedPreferencesDirectory(dataDir, true);
            temp = File.createTempFile(".PhoneDeckDevices-migration-", ".tmp", sharedPrefsDir);
            try (FileOutputStream output = new FileOutputStream(temp)) {
                output.write(xml);
                output.flush();
                output.getFD().sync();
            }

            // Verify the exact temporary file before it becomes the canonical
            // SharedPreferences file.  This catches serializer or filesystem
            // corruption without touching an existing target.
            TargetState staged = readTargetState(temp, keyAccess.key);
            if (!samePayload(payload, staged)) {
                throw failure("staged_preferences_mismatch");
            }
            if (pathExistsNoFollow(target)) {
                throw failure("target_created_during_migration");
            }
            publishCreateNewAndForce(temp, target);
            try {
                TargetState committed = readTargetState(target, keyAccess.key);
                if (!samePayload(payload, committed)) {
                    throw failure("target_publish_verify_failed_recovery_required");
                }
            } catch (MigrationFailure migrationFailure) {
                throw failure("target_publish_verify_failed_recovery_required");
            } catch (Exception exception) {
                throw failure("target_publish_verify_failed_recovery_required");
            }
            writeSummary("import", payload,
                    sha256(canonicalPayload(payload).getBytes(StandardCharsets.UTF_8)),
                    true, keyAccess.created, sealed.length);
        } catch (MigrationFailure migrationFailure) {
            if (keyAccess.created) {
                throw failure("migration_failed_key_retained_" + migrationFailure.code);
            }
            throw migrationFailure;
        } catch (Exception exception) {
            if (keyAccess.created) {
                throw failure("migration_failed_key_retained_"
                        + exception.getClass().getSimpleName());
            }
            throw exception;
        } finally {
            if (temp != null && temp.exists() && !temp.delete()) {
                // The failure path must not remove any target or Keystore key.
                // There is no safe recovery action for a temp file owned by
                // this one-shot process, so leave it for the caller to report.
            }
            clear(devicesPlain);
            clear(sealed);
            clear(xml);
        }
    }

    private static void verifyPayload(File dataDir, boolean base64Stdin) throws Exception {
        Payload payload = readPayloadFromStdin(base64Stdin);
        installAndroidKeyStoreProvider();
        File target = preferencesFile(dataDir, false);
        if (!pathExistsNoFollow(target)) {
            throw failure("target_preferences_missing");
        }
        SecretKey key = loadExistingKey();
        TargetState targetState = readTargetState(target, key);
        if (!samePayload(payload, targetState)) {
            throw failure("target_payload_mismatch");
        }
        writeSummary("verify", payload,
                sha256(canonicalPayload(payload).getBytes(StandardCharsets.UTF_8)),
                true, false, targetState.encryptedBytes);
    }

    private static Payload readPayloadFromStdin(boolean base64Stdin) throws Exception {
        byte[] stdin = readAtMost(System.in, MAX_JSON_BYTES * 2);
        byte[] framed = stdin;
        try {
            if (base64Stdin) {
                String encoded = decodeUtf8(stdin);
                if (encoded.length() == 0 || !encoded.matches("[A-Za-z0-9+/]*={0,2}")) {
                    throw failure("stdin_base64_invalid");
                }
                framed = decodeBase64(encoded);
            }
            String text = decodeUtf8(framed);
            String json = unframe(text);
            return parsePayload(json);
        } finally {
            if (framed != stdin) {
                clear(framed);
            }
            clear(stdin);
        }
    }

    private static String unframe(String text) throws MigrationFailure {
        String prefix = FRAME_START + "\n";
        String suffix = "\n" + FRAME_END;
        if (!text.startsWith(prefix) || !text.endsWith(suffix + "\n")
                && !text.endsWith(suffix)) {
            throw failure("frame_markers_invalid");
        }
        int jsonStart = prefix.length();
        int jsonEnd = text.endsWith(suffix + "\n")
                ? text.length() - (suffix.length() + 1)
                : text.length() - suffix.length();
        if (jsonEnd <= jsonStart) {
            throw failure("frame_payload_missing");
        }
        String json = text.substring(jsonStart, jsonEnd);
        if (json.indexOf('\n') >= 0 || json.indexOf('\r') >= 0) {
            throw failure("frame_payload_multiline");
        }
        return json;
    }

    private static Payload parsePayload(String json) throws Exception {
        if (json.getBytes(StandardCharsets.UTF_8).length > MAX_JSON_BYTES) {
            throw failure("payload_too_large");
        }
        JSONObject object;
        try {
            object = new JSONObject(json);
        } catch (Exception exception) {
            throw failure("payload_json_invalid");
        }
        requireExactFields(object, PAYLOAD_FIELDS, "payload_fields_invalid");
        if (requireInt(object, "format") != FORMAT) {
            throw failure("payload_format_invalid");
        }
        String packageName = requireString(object, "package", false);
        if (!PACKAGE_NAME.equals(packageName)) {
            throw failure("payload_package_invalid");
        }
        long uid = requireLong(object, "uid");
        if (uid <= 0 || uid > Integer.MAX_VALUE) {
            throw failure("payload_uid_invalid");
        }
        String active = requireString(object, "activeComputerId", true);
        JSONArray array = requireArray(object, "devices");
        List<DeviceRecord> devices = parseDevices(array);
        validateActiveComputerId(active, devices);
        return new Payload(uid, active, devices);
    }

    private static List<DeviceRecord> parseDevicesJson(String json) throws Exception {
        if (json == null || json.getBytes(StandardCharsets.UTF_8).length > MAX_JSON_BYTES) {
            throw failure("device_json_invalid");
        }
        try {
            return parseDevices(new JSONArray(json));
        } catch (MigrationFailure failure) {
            throw failure;
        } catch (Exception exception) {
            throw failure("device_json_invalid");
        }
    }

    private static List<DeviceRecord> parseDevices(JSONArray array) throws Exception {
        if (array.length() > MAX_DEVICES) {
            throw failure("device_count_invalid");
        }
        ArrayList<DeviceRecord> devices = new ArrayList<>();
        HashSet<String> ids = new HashSet<>();
        HashSet<Integer> slots = new HashSet<>();
        for (int index = 0; index < array.length(); index++) {
            Object value = array.get(index);
            if (!(value instanceof JSONObject)) {
                throw failure("device_record_invalid");
            }
            DeviceRecord device = parseDevice((JSONObject) value);
            String idKey = device.computerId.toLowerCase(Locale.ROOT);
            if (!ids.add(idKey)) {
                throw failure("duplicate_computer_id");
            }
            if (!slots.add(device.slot)) {
                throw failure("duplicate_device_slot");
            }
            devices.add(device);
        }
        return devices;
    }

    private static DeviceRecord parseDevice(JSONObject object) throws Exception {
        requireExactFields(object, DEVICE_FIELDS, "device_fields_invalid");
        String computerId = requireString(object, "computerId", false);
        String displayName = requireString(object, "displayName", false);
        String platform = requireString(object, "platform", false);
        int slot = requireInt(object, "slot");
        long lastSeenAt = requireLong(object, "lastSeenAt");
        JSONArray addressArray = requireArray(object, "lanAddresses");
        int lanPort = requireInt(object, "lanPort");
        String lanToken = requireString(object, "lanToken", true);
        String clientId = requireString(object, "clientId", true);
        boolean sharedGroup = requireBoolean(object, "sharedGroup");
        String certificateSha256 = requireString(object, "certificateSha256", true);
        String lastGoodAddress = requireString(object, "lastGoodAddress", true);
        boolean nameLocked = requireBoolean(object, "nameLocked");

        if (computerId.trim().isEmpty() || computerId.length() > 256
                || displayName.trim().isEmpty() || displayName.length() > 256
                || platform.trim().isEmpty() || platform.length() > 64) {
            throw failure("device_text_invalid");
        }
        if (slot < 1 || slot > MAX_DEVICES || lastSeenAt < 0
                || lanPort < 0 || lanPort > 65535) {
            throw failure("device_number_invalid");
        }
        if (lanToken != null && (lanToken.length() == 0 || lanToken.length() > 4096)
                || clientId != null && (clientId.length() == 0 || clientId.length() > 512)
                || certificateSha256 != null
                && (certificateSha256.length() == 0 || certificateSha256.length() > 512)) {
            throw failure("device_credential_field_invalid");
        }

        if (addressArray.length() > MAX_ADDRESSES) {
            throw failure("address_count_invalid");
        }
        ArrayList<String> addresses = new ArrayList<>();
        HashSet<String> addressSet = new HashSet<>();
        for (int index = 0; index < addressArray.length(); index++) {
            Object value = addressArray.get(index);
            if (!(value instanceof String)) {
                throw failure("address_invalid");
            }
            String address = (String) value;
            if (address.length() == 0 || address.length() > 255
                    || !address.equals(address.trim()) || !safeAddress(address)
                    || !addressSet.add(address)) {
                throw failure("address_invalid");
            }
            addresses.add(address);
        }
        if (lastGoodAddress != null
                && (lastGoodAddress.length() == 0 || lastGoodAddress.length() > 255
                || !lastGoodAddress.equals(lastGoodAddress.trim())
                || !safeAddress(lastGoodAddress))) {
            throw failure("last_good_address_invalid");
        }
        return new DeviceRecord(computerId, displayName, platform, slot, lastSeenAt,
                addresses, lanPort, lanToken, clientId, sharedGroup,
                certificateSha256, lastGoodAddress, nameLocked);
    }

    private static void validateActiveComputerId(String active, List<DeviceRecord> devices)
            throws MigrationFailure {
        if (active == null) {
            if (!devices.isEmpty()) {
                throw failure("active_computer_missing");
            }
            return;
        }
        if (active.trim().isEmpty() || active.length() > 256) {
            throw failure("active_computer_invalid");
        }
        for (DeviceRecord device : devices) {
            if (device.computerId.equalsIgnoreCase(active)) {
                return;
            }
        }
        throw failure("active_computer_unknown");
    }

    private static String canonicalPayload(Payload payload) {
        StringBuilder builder = new StringBuilder();
        builder.append("{\"format\":").append(FORMAT)
                .append(",\"package\":").append(JSONObject.quote(PACKAGE_NAME))
                .append(",\"uid\":").append(payload.uid)
                .append(",\"activeComputerId\":");
        appendNullable(builder, payload.activeComputerId);
        builder.append(",\"devices\":").append(canonicalDevices(payload.devices)).append('}');
        return builder.toString();
    }

    private static String canonicalDevices(List<DeviceRecord> devices) {
        StringBuilder builder = new StringBuilder();
        builder.append('[');
        for (int index = 0; index < devices.size(); index++) {
            if (index > 0) {
                builder.append(',');
            }
            builder.append(devices.get(index).canonicalJson());
        }
        return builder.append(']').toString();
    }

    private static byte[] frame(String canonicalPayload) {
        String framed = FRAME_START + "\n" + canonicalPayload + "\n" + FRAME_END + "\n";
        return framed.getBytes(StandardCharsets.UTF_8);
    }

    private static byte[] preferencesXml(String activeComputerId, byte[] sealed)
            throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream(256 + sealed.length * 2);
        XmlSerializer serializer = Xml.newSerializer();
        serializer.setOutput(output, "UTF-8");
        serializer.startDocument("UTF-8", true);
        serializer.startTag(null, "map");
        if (activeComputerId != null) {
            writeStringPreference(serializer, KEY_ACTIVE, activeComputerId);
        }
        writeStringPreference(serializer, KEY_DEVICES_ENCRYPTED,
                Base64.encodeToString(sealed, Base64.NO_WRAP));
        serializer.endTag(null, "map");
        serializer.endDocument();
        serializer.flush();
        return output.toByteArray();
    }

    private static void writeStringPreference(XmlSerializer serializer, String name, String value)
            throws Exception {
        serializer.startTag(null, "string");
        serializer.attribute(null, "name", name);
        serializer.text(value);
        serializer.endTag(null, "string");
    }

    private static TargetState readTargetState(File file, SecretKey key) throws Exception {
        Preferences preferences = readPreferences(file);
        rejectUnsupportedPreferenceRecords(preferences);
        if (preferences.values.containsKey(KEY_DEVICES)) {
            throw failure("plaintext_fallback_present");
        }
        String sealedText = preferences.values.get(KEY_DEVICES_ENCRYPTED);
        if (sealedText == null) {
            throw failure("encrypted_record_missing");
        }
        byte[] sealed = decodeBase64(sealedText);
        byte[] plain = null;
        try {
            plain = decrypt(key, sealed);
            List<DeviceRecord> devices = parseDevicesJson(decodeUtf8(plain));
            String active = preferences.values.get(KEY_ACTIVE);
            validateActiveComputerId(active, devices);
            return new TargetState(active, devices, sealed.length);
        } finally {
            clear(sealed);
            clear(plain);
        }
    }

    private static void rejectUnsupportedPreferenceRecords(Preferences preferences)
            throws MigrationFailure {
        for (String name : preferences.values.keySet()) {
            if (!KEY_ACTIVE.equals(name) && !KEY_DEVICES.equals(name)
                    && !KEY_DEVICES_ENCRYPTED.equals(name)) {
                throw failure("unsupported_preferences_record");
            }
        }
    }

    private static boolean samePayload(Payload payload, TargetState state) {
        return equalNullable(payload.activeComputerId, state.activeComputerId)
                && canonicalDevices(payload.devices).equals(canonicalDevices(state.devices));
    }

    private static Preferences readPreferences(File file) throws Exception {
        if (!file.exists() || !file.isFile()) {
            throw failure("preferences_file_missing");
        }
        LinkedHashMap<String, String> values = new LinkedHashMap<>();
        try (FileInputStream input = new FileInputStream(file)) {
            XmlPullParser parser = Xml.newPullParser();
            parser.setInput(input, "UTF-8");
            boolean rootSeen = false;
            for (int event = parser.getEventType(); event != XmlPullParser.END_DOCUMENT;
                 event = parser.next()) {
                if (event != XmlPullParser.START_TAG) {
                    continue;
                }
                String tag = parser.getName();
                if (!rootSeen) {
                    if (!"map".equals(tag)) {
                        throw failure("preferences_root_invalid");
                    }
                    rootSeen = true;
                    continue;
                }
                if (parser.getDepth() != 2 || !"string".equals(tag)) {
                    throw failure("preferences_entry_invalid");
                }
                String name = parser.getAttributeValue(null, "name");
                if (name == null || name.length() == 0 || values.containsKey(name)) {
                    throw failure("preferences_name_invalid");
                }
                String value = parser.nextText();
                if (value == null) {
                    throw failure("preferences_value_invalid");
                }
                values.put(name, value);
            }
            if (!rootSeen) {
                throw failure("preferences_root_missing");
            }
        } catch (MigrationFailure failure) {
            throw failure;
        } catch (Exception exception) {
            throw failure("preferences_xml_invalid");
        }
        return new Preferences(values);
    }

    private static File validateDataDir(File supplied) throws Exception {
        if (supplied == null) {
            throw failure("data_directory_missing");
        }
        File dataDir = supplied.getCanonicalFile();
        // run-as supplies the app-private directory.  Requiring the package
        // leaf prevents an accidental path to shared/external storage; the
        // kernel UID check remains the authoritative boundary.
        if (!PACKAGE_NAME.equals(dataDir.getName()) || !dataDir.isDirectory()) {
            throw failure("data_directory_invalid");
        }
        return dataDir;
    }

    private static File preferencesFile(File dataDir, boolean createDirectory) throws Exception {
        File directory = sharedPreferencesDirectory(dataDir, createDirectory);
        // Keep the final path lexical so a broken symlink is still visible to
        // the NOFOLLOW_LINKS checks below.
        File result = new File(directory, PREFS_FILE).getAbsoluteFile();
        String parent = directory.getCanonicalPath() + File.separator;
        if (!result.getPath().startsWith(parent)) {
            throw failure("preferences_path_invalid");
        }
        return result;
    }

    private static File sharedPreferencesDirectory(File dataDir, boolean createDirectory)
            throws Exception {
        File directory = new File(dataDir, "shared_prefs");
        if (!directory.exists() && createDirectory && !directory.mkdirs()) {
            throw failure("preferences_directory_unavailable");
        }
        if (!directory.exists() || !directory.isDirectory()) {
            throw failure("preferences_directory_invalid");
        }
        File canonical = directory.getCanonicalFile();
        String root = dataDir.getCanonicalPath() + File.separator;
        if (!canonical.getPath().startsWith(root)) {
            throw failure("preferences_directory_outside_app");
        }
        return canonical;
    }

    private static boolean pathExistsNoFollow(File file) {
        return Files.exists(file.toPath(), LinkOption.NOFOLLOW_LINKS);
    }

    private static KeyAccess loadOrCreateKeyOnlyWhenTargetAbsent(File target) throws Exception {
        if (pathExistsNoFollow(target)) {
            throw failure("target_preferences_already_exists");
        }
        KeyStore store = KeyStore.getInstance(ANDROID_KEYSTORE);
        store.load(null);
        if (!store.containsAlias(KEY_ALIAS)) {
            if (pathExistsNoFollow(target)) {
                throw failure("target_created_before_key_generation");
            }
            KeyGenerator generator = KeyGenerator.getInstance(
                    KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE);
            generator.init(new KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .setRandomizedEncryptionRequired(true)
                    .build());
            SecretKey generated = generator.generateKey();
            if (generated == null) {
                throw failure("keystore_key_generation_failed");
            }
            return new KeyAccess(generated, true);
        }
        SecretKey key = getSecretKey(store);
        return new KeyAccess(key, false);
    }

    private static SecretKey loadExistingKey() throws Exception {
        KeyStore store = KeyStore.getInstance(ANDROID_KEYSTORE);
        store.load(null);
        if (!store.containsAlias(KEY_ALIAS)) {
            throw failure("keystore_alias_missing");
        }
        return getSecretKey(store);
    }

    private static SecretKey getSecretKey(KeyStore store) throws Exception {
        KeyStore.Entry entry = store.getEntry(KEY_ALIAS, null);
        if (!(entry instanceof KeyStore.SecretKeyEntry)) {
            throw failure("keystore_alias_not_secret_key");
        }
        SecretKey key = ((KeyStore.SecretKeyEntry) entry).getSecretKey();
        if (key == null) {
            throw failure("keystore_key_unavailable");
        }
        return key;
    }

    private static void installAndroidKeyStoreProvider() throws Exception {
        if (Security.getProvider(ANDROID_KEYSTORE) != null) {
            return;
        }
        String[] providerClasses = {
                // Android 12+/SDK 31 app_process path.
                "android.security.keystore2.AndroidKeyStoreProvider",
                // Older platform fallback, kept reflective so the helper
                // remains loadable on API 26 devices.
                "android.security.keystore.AndroidKeyStoreProvider"
        };
        for (String className : providerClasses) {
            try {
                Class<?> provider = Class.forName(className);
                provider.getMethod("install").invoke(null);
                if (Security.getProvider(ANDROID_KEYSTORE) != null) {
                    return;
                }
            } catch (Throwable ignored) {
                // Try the next platform implementation without exposing the
                // provider exception, which can contain package details.
            }
        }
        throw failure("keystore_provider_unavailable");
    }

    private static byte[] encrypt(SecretKey key, byte[] plain) throws Exception {
        Cipher cipher = Cipher.getInstance(CIPHER_TRANSFORMATION);
        cipher.init(Cipher.ENCRYPT_MODE, key);
        byte[] ciphertext = cipher.doFinal(plain);
        byte[] iv = cipher.getIV();
        if (iv == null || iv.length != IV_BYTES) {
            clear(ciphertext);
            throw failure("keystore_iv_invalid");
        }
        byte[] sealed = new byte[iv.length + ciphertext.length];
        System.arraycopy(iv, 0, sealed, 0, iv.length);
        System.arraycopy(ciphertext, 0, sealed, iv.length, ciphertext.length);
        clear(ciphertext);
        return sealed;
    }

    private static byte[] decrypt(SecretKey key, byte[] sealed) throws Exception {
        if (sealed == null || sealed.length <= IV_BYTES + TAG_BITS / 8) {
            throw failure("ciphertext_too_short");
        }
        Cipher cipher = Cipher.getInstance(CIPHER_TRANSFORMATION);
        GCMParameterSpec parameters = new GCMParameterSpec(TAG_BITS, sealed, 0, IV_BYTES);
        cipher.init(Cipher.DECRYPT_MODE, key, parameters);
        return cipher.doFinal(sealed, IV_BYTES, sealed.length - IV_BYTES);
    }

    private static void publishCreateNewAndForce(File source, File target) throws Exception {
        try {
            // CREATE_NEW is an exclusive no-overwrite create.  Android API
            // 31 emulators do not consistently support hard-link publication,
            // so this deliberately uses a durable copy and an explicit
            // post-write decrypt/field comparison instead of claiming an
            // atomic rename.
            try (FileChannel input = FileChannel.open(
                         source.toPath(), StandardOpenOption.READ);
                 FileChannel output = FileChannel.open(
                         target.toPath(), StandardOpenOption.CREATE_NEW,
                         StandardOpenOption.WRITE)) {
                long size = input.size();
                long position = 0L;
                while (position < size) {
                    long transferred = input.transferTo(position, size - position, output);
                    if (transferred > 0L) {
                        position += transferred;
                        continue;
                    }
                    input.position(position);
                    ByteBuffer buffer = ByteBuffer.allocate(8192);
                    int read = input.read(buffer);
                    if (read < 0) {
                        throw failure("target_publish_failed_recovery_required");
                    }
                    buffer.flip();
                    while (buffer.hasRemaining()) {
                        output.write(buffer);
                    }
                    position += read;
                }
                output.force(true);
            }
        } catch (FileAlreadyExistsException exception) {
            throw failure("target_created_during_migration_recovery_required");
        } catch (IOException exception) {
            throw failure("target_publish_failed_recovery_required");
        }
    }

    private static byte[] readAtMost(InputStream input, int limit) throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        int count;
        while ((count = input.read(buffer)) != -1) {
            total += count;
            if (total > limit) {
                throw failure("stdin_too_large");
            }
            output.write(buffer, 0, count);
        }
        return output.toByteArray();
    }

    private static String decodeUtf8(byte[] bytes) throws MigrationFailure {
        try {
            return StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(java.nio.ByteBuffer.wrap(bytes)).toString();
        } catch (CharacterCodingException exception) {
            throw failure("utf8_invalid");
        }
    }

    private static byte[] decodeBase64(String encoded) throws MigrationFailure {
        if (encoded == null || encoded.length() == 0 || (encoded.length() & 3) != 0
                || !encoded.matches("[A-Za-z0-9+/]*={0,2}")) {
            throw failure("base64_invalid");
        }
        byte[] decoded;
        try {
            decoded = Base64.decode(encoded, Base64.DEFAULT);
        } catch (IllegalArgumentException exception) {
            throw failure("base64_invalid");
        }
        String canonical = Base64.encodeToString(decoded, Base64.NO_WRAP);
        if (!encoded.equals(canonical)) {
            clear(decoded);
            throw failure("base64_not_canonical");
        }
        return decoded;
    }

    private static void requireExactFields(JSONObject object, String[] expected, String code)
            throws MigrationFailure {
        Set<String> expectedSet = new HashSet<>();
        for (String field : expected) {
            expectedSet.add(field);
            if (!object.has(field)) {
                throw failure(code);
            }
        }
        if (object.length() != expected.length) {
            throw failure(code);
        }
        Iterator<String> keys = object.keys();
        while (keys.hasNext()) {
            if (!expectedSet.contains(keys.next())) {
                throw failure(code);
            }
        }
    }

    private static String requireString(JSONObject object, String field, boolean nullable)
            throws Exception {
        Object value = object.get(field);
        if (value == JSONObject.NULL) {
            if (nullable) {
                return null;
            }
            throw failure("required_string_missing");
        }
        if (!(value instanceof String)) {
            throw failure("string_field_invalid");
        }
        return (String) value;
    }

    private static int requireInt(JSONObject object, String field) throws Exception {
        long value = requireLong(object, field);
        if (value < Integer.MIN_VALUE || value > Integer.MAX_VALUE) {
            throw failure("integer_field_invalid");
        }
        return (int) value;
    }

    private static long requireLong(JSONObject object, String field) throws Exception {
        Object value = object.get(field);
        if (!(value instanceof Number)) {
            throw failure("number_field_invalid");
        }
        Number number = (Number) value;
        long result = number.longValue();
        if (number.doubleValue() != (double) result) {
            throw failure("integer_field_invalid");
        }
        return result;
    }

    private static boolean requireBoolean(JSONObject object, String field) throws Exception {
        Object value = object.get(field);
        if (!(value instanceof Boolean)) {
            throw failure("boolean_field_invalid");
        }
        return (Boolean) value;
    }

    private static JSONArray requireArray(JSONObject object, String field) throws Exception {
        Object value = object.get(field);
        if (!(value instanceof JSONArray)) {
            throw failure("array_field_invalid");
        }
        return (JSONArray) value;
    }

    private static boolean safeAddress(String address) {
        return address != null && !address.startsWith("127.")
                && !address.startsWith("169.254.")
                && !address.startsWith("198.18.")
                && !address.startsWith("198.19.");
    }

    private static void appendNullable(StringBuilder builder, String value) {
        builder.append(value == null ? "null" : JSONObject.quote(value));
    }

    private static boolean equalNullable(String first, String second) {
        return first == null ? second == null : first.equals(second);
    }

    private static boolean constantTimeEquals(byte[] first, byte[] second) {
        if (first == null || second == null) {
            return first == second;
        }
        int difference = first.length ^ second.length;
        int common = Math.min(first.length, second.length);
        for (int index = 0; index < common; index++) {
            difference |= first[index] ^ second[index];
        }
        return difference == 0;
    }

    private static String sha256(byte[] bytes) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(bytes);
        StringBuilder result = new StringBuilder(digest.length * 2);
        for (byte value : digest) {
            result.append(String.format(Locale.ROOT, "%02x", value & 0xff));
        }
        clear(digest);
        return result.toString();
    }

    private static void writeSummary(String mode, Payload payload, String hash,
                                     boolean targetPresent, boolean keyCreated,
                                     int encryptedBytes) throws MigrationFailure {
        String summary = "{\"ok\":true,\"mode\":" + JSONObject.quote(mode)
                + ",\"deviceCount\":" + payload.devices.size()
                + ",\"activePresent\":" + (payload.activeComputerId != null)
                + ",\"payloadSha256\":" + JSONObject.quote(hash)
                + ",\"targetPresent\":" + targetPresent
                + ",\"keyCreated\":" + keyCreated
                + ",\"encryptedBytes\":" + encryptedBytes + "}\n";
        writeUtf8(System.err, summary);
    }

    private static void writeError(String mode, String code) {
        String error = "{\"ok\":false,\"mode\":" + JSONObject.quote(mode)
                + ",\"error\":" + JSONObject.quote(code) + "}\n";
        try {
            writeUtf8(System.err, error);
        } catch (MigrationFailure ignored) {
        }
    }

    private static void writeBytes(OutputStream output, byte[] bytes) throws MigrationFailure {
        try {
            output.write(bytes);
            output.flush();
        } catch (IOException exception) {
            throw failure("output_write_failed");
        }
        if (output instanceof PrintStream && ((PrintStream) output).checkError()) {
            throw failure("output_write_failed");
        }
    }

    private static void writeUtf8(OutputStream output, String text) throws MigrationFailure {
        writeBytes(output, text.getBytes(StandardCharsets.UTF_8));
    }

    private static void clear(byte[] bytes) {
        if (bytes != null) {
            java.util.Arrays.fill(bytes, (byte) 0);
        }
    }

    private static String normalizeMode(String mode) {
        return "export".equals(mode) || "import".equals(mode) || "verify".equals(mode)
                ? mode : "unknown";
    }

    private static MigrationFailure failure(String code) {
        return new MigrationFailure(code);
    }

    private static final class MigrationFailure extends Exception {
        private static final long serialVersionUID = 1L;
        final String code;

        MigrationFailure(String code) {
            this.code = code;
        }
    }

    private static final class Preferences {
        final Map<String, String> values;

        Preferences(Map<String, String> values) {
            this.values = values;
        }
    }

    private static final class Payload {
        final long uid;
        final String activeComputerId;
        final List<DeviceRecord> devices;

        Payload(long uid, String activeComputerId, List<DeviceRecord> devices) {
            this.uid = uid;
            this.activeComputerId = activeComputerId;
            this.devices = devices;
        }
    }

    private static final class TargetState {
        final String activeComputerId;
        final List<DeviceRecord> devices;
        final int encryptedBytes;

        TargetState(String activeComputerId, List<DeviceRecord> devices, int encryptedBytes) {
            this.activeComputerId = activeComputerId;
            this.devices = devices;
            this.encryptedBytes = encryptedBytes;
        }
    }

    private static final class KeyAccess {
        final SecretKey key;
        final boolean created;

        KeyAccess(SecretKey key, boolean created) {
            this.key = key;
            this.created = created;
        }
    }

    private static final class DeviceRecord {
        final String computerId;
        final String displayName;
        final String platform;
        final int slot;
        final long lastSeenAt;
        final List<String> lanAddresses;
        final int lanPort;
        final String lanToken;
        final String clientId;
        final boolean sharedGroup;
        final String certificateSha256;
        final String lastGoodAddress;
        final boolean nameLocked;

        DeviceRecord(String computerId, String displayName, String platform, int slot,
                     long lastSeenAt, List<String> lanAddresses, int lanPort,
                     String lanToken, String clientId, boolean sharedGroup,
                     String certificateSha256, String lastGoodAddress, boolean nameLocked) {
            this.computerId = computerId;
            this.displayName = displayName;
            this.platform = platform;
            this.slot = slot;
            this.lastSeenAt = lastSeenAt;
            this.lanAddresses = new ArrayList<>(lanAddresses);
            this.lanPort = lanPort;
            this.lanToken = lanToken;
            this.clientId = clientId;
            this.sharedGroup = sharedGroup;
            this.certificateSha256 = certificateSha256;
            this.lastGoodAddress = lastGoodAddress;
            this.nameLocked = nameLocked;
        }

        String canonicalJson() {
            StringBuilder builder = new StringBuilder();
            builder.append("{\"computerId\":").append(JSONObject.quote(computerId))
                    .append(",\"displayName\":").append(JSONObject.quote(displayName))
                    .append(",\"platform\":").append(JSONObject.quote(platform))
                    .append(",\"slot\":").append(slot)
                    .append(",\"lastSeenAt\":").append(lastSeenAt)
                    .append(",\"lanAddresses\":[");
            for (int index = 0; index < lanAddresses.size(); index++) {
                if (index > 0) {
                    builder.append(',');
                }
                builder.append(JSONObject.quote(lanAddresses.get(index)));
            }
            builder.append("],\"lanPort\":").append(lanPort)
                    .append(",\"lanToken\":");
            appendNullable(builder, lanToken);
            builder.append(",\"clientId\":");
            appendNullable(builder, clientId);
            builder.append(",\"sharedGroup\":").append(sharedGroup)
                    .append(",\"certificateSha256\":");
            appendNullable(builder, certificateSha256);
            builder.append(",\"lastGoodAddress\":");
            appendNullable(builder, lastGoodAddress);
            builder.append(",\"nameLocked\":").append(nameLocked).append('}');
            return builder.toString();
        }
    }
}
