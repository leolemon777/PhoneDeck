package com.codex.phonedeck;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.X509EncodedKeySpec;
import java.util.*;
import java.util.zip.*;

/** Publisher signatures are independent from the LAN peer's pairing credentials. */
final class UpdateBundle {
    static final long MAX_BYTES = 384L * 1024 * 1024;
    static final long MAX_FILE = 192L * 1024 * 1024;
    static final Set<String> FILES = new HashSet<>(Arrays.asList(
            "PhoneDeck.Server.exe", "PhoneDeck.ControlCenter.exe", "PhoneDeck.apk"));
    final JSONObject manifest;
    final long sequence;
    final int androidVersion;
    final String windowsVersion;

    private UpdateBundle(JSONObject manifest) throws Exception {
        this.manifest = manifest;
        sequence = manifest.getLong("sequence");
        androidVersion = manifest.getInt("androidVersionCode");
        windowsVersion = manifest.getString("windowsVersion");
        if (manifest.getInt("schema") != 1 || sequence < 1 || androidVersion < 1
                || windowsVersion.isBlank() || windowsVersion.length() > 64)
            throw new IOException("不支持的更新清单");
    }

    static UpdateBundle verify(File file, String publicKey, File apk) throws Exception {
        if (file.length() > MAX_BYTES) throw new IOException("更新包过大");
        try (ZipFile zip = new ZipFile(file)) {
            Set<String> expected = new HashSet<>(FILES);
            expected.add("manifest.json"); expected.add("manifest.sig");
            Enumeration<? extends ZipEntry> entries = zip.entries();
            while (entries.hasMoreElements())
                if (!expected.remove(entries.nextElement().getName()))
                    throw new IOException("更新包包含重复或未允许的路径");
            if (!expected.isEmpty()) throw new IOException("更新包不完整");
            byte[] bytes = readSmall(zip, "manifest.json", 16384);
            verifySignature(bytes, readSmall(zip, "manifest.sig", 1024), publicKey);
            UpdateBundle result = new UpdateBundle(new JSONObject(new String(bytes, StandardCharsets.UTF_8)));
            JSONArray files = result.manifest.getJSONArray("files");
            expected = new HashSet<>(FILES);
            long total = 0;
            for (int i = 0; i < files.length(); i++) {
                JSONObject artifact = files.getJSONObject(i);
                String name = artifact.getString("name");
                if (!expected.remove(name)) throw new IOException("清单文件不匹配");
                long size = artifact.getLong("size");
                total += size;
                ZipEntry entry = zip.getEntry(name);
                if (size < 1 || size > MAX_FILE || entry.getSize() != size
                        || total > 512L * 1024 * 1024) throw new IOException("文件大小超限或不匹配");
                MessageDigest digest = MessageDigest.getInstance("SHA-256");
                try (InputStream input = zip.getInputStream(entry)) {
                    byte[] buffer = new byte[65536]; int count; long actual = 0;
                    while ((count = input.read(buffer)) != -1) {
                        actual += count;
                        if (actual > size) throw new IOException("解压大小超限");
                        digest.update(buffer, 0, count);
                    }
                    if (actual != size) throw new IOException("文件截断");
                }
                if (!hex(digest.digest()).equalsIgnoreCase(artifact.getString("sha256")))
                    throw new IOException("文件校验失败：" + name);
            }
            if (!expected.isEmpty()) throw new IOException("清单不完整");
            if (apk != null) {
                File temp = new File(apk.getParentFile(), "PhoneDeck.apk.partial");
                try (InputStream input = zip.getInputStream(zip.getEntry("PhoneDeck.apk"));
                     OutputStream output = new FileOutputStream(temp)) { copy(input, output, MAX_FILE); }
                if (apk.exists() && !apk.delete()) throw new IOException("无法替换缓存安装包");
                if (!temp.renameTo(apk)) throw new IOException("无法保存安装包");
            }
            return result;
        }
    }

    static void verifySignature(byte[] data, byte[] signature, String publicKey) throws Exception {
        Signature verifier = Signature.getInstance("SHA256withRSA");
        verifier.initVerify(KeyFactory.getInstance("RSA").generatePublic(
                new X509EncodedKeySpec(Base64.getDecoder().decode(publicKey.trim()))));
        verifier.update(data);
        if (!verifier.verify(signature)) throw new IOException("发布者签名不匹配，拒绝安装");
    }

    static void copy(InputStream input, OutputStream output, long max) throws IOException {
        byte[] buffer = new byte[65536]; int count; long size = 0;
        while ((count = input.read(buffer)) != -1) {
            size += count;
            if (size > max) throw new IOException("下载大小超限");
            output.write(buffer, 0, count);
        }
    }

    private static byte[] readSmall(ZipFile zip, String name, int max) throws IOException {
        try (InputStream input = zip.getInputStream(zip.getEntry(name));
             ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            copy(input, output, max); return output.toByteArray();
        }
    }

    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder();
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 255));
        return result.toString();
    }
}
