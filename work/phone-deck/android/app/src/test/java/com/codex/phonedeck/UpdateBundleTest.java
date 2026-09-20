package com.codex.phonedeck;

import org.junit.Test;
import java.io.*;
import java.security.*;
import java.util.Base64;
import static org.junit.Assert.*;

public final class UpdateBundleTest {
    @Test public void acceptsPublisherAndRejectsModifiedManifest() throws Exception {
        KeyPairGenerator generator = KeyPairGenerator.getInstance("RSA"); generator.initialize(2048);
        KeyPair key = generator.generateKeyPair();
        String publicKey = Base64.getEncoder().encodeToString(key.getPublic().getEncoded());
        byte[] manifest = "signed release manifest".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        Signature signer = Signature.getInstance("SHA256withRSA"); signer.initSign(key.getPrivate()); signer.update(manifest);
        byte[] signature = signer.sign();
        UpdateBundle.verifySignature(manifest, signature, publicKey);
        manifest[0] ^= 1;
        try { UpdateBundle.verifySignature(manifest, signature, publicKey); fail("must reject modified manifest"); }
        catch (IOException expected) { }
    }
    @Test public void downloadRejectsOversizedBody() throws Exception {
        try {
            UpdateBundle.copy(new ByteArrayInputStream(new byte[1025]), new ByteArrayOutputStream(), 1024);
            fail("must enforce limit even without Content-Length");
        } catch (IOException expected) { }
    }
}
