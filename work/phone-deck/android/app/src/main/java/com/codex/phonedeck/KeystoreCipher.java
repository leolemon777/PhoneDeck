package com.codex.phonedeck;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;

import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * M1-A A3（设计 §7）：AndroidKeyStore AES-256-GCM 封装。
 * 密钥常驻 Keystore 不可导出；每次加密 fresh IV，输出 = 12 字节 IV ‖ 密文+tag。
 * 仅用于 SharedPreferences 中凭据记录的封装；失败时由调用方决定回退（可用性优先）。
 */
final class KeystoreCipher {
    private static final String ANDROID_KEYSTORE = "AndroidKeyStore";
    private static final String KEY_ALIAS = "phonedeck_device_store";
    private static final int GCM_TAG_BITS = 128;
    private static final int IV_BYTES = 12;

    private KeystoreCipher() {
    }

    static byte[] encrypt(byte[] plain) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] sealed = cipher.doFinal(plain);
        byte[] iv = cipher.getIV();
        byte[] output = new byte[iv.length + sealed.length];
        System.arraycopy(iv, 0, output, 0, iv.length);
        System.arraycopy(sealed, 0, output, iv.length, sealed.length);
        return output;
    }

    static byte[] decrypt(byte[] sealed) throws Exception {
        if (sealed == null || sealed.length <= IV_BYTES) {
            throw new IllegalArgumentException("加密载荷过短");
        }
        GCMParameterSpec spec = new GCMParameterSpec(GCM_TAG_BITS, sealed, 0, IV_BYTES);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), spec);
        return cipher.doFinal(sealed, IV_BYTES, sealed.length - IV_BYTES);
    }

    private static SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance(ANDROID_KEYSTORE);
        store.load(null);
        KeyStore.Entry entry = store.getEntry(KEY_ALIAS, null);
        if (entry instanceof KeyStore.SecretKeyEntry) {
            return ((KeyStore.SecretKeyEntry) entry).getSecretKey();
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
        return generator.generateKey();
    }
}
