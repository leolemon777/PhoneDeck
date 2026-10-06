package com.codex.phonedeck;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

/** 与电脑端 NearbyPairingTests.CheckCodeMatchesSharedVector 同一固定向量。 */
public class NearbyPairingClientTest {
    private static final String CERT = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static final String CLIENT = "11111111-1111-4111-8111-111111111111";
    private static final String NONCE = "0123456789abcdef0123456789abcdef";

    @Test
    public void checkCodeMatchesReceiverVector() {
        assertEquals("3120", NearbyPairingClient.checkCode(CERT, CLIENT, NONCE));
        assertEquals("3120", NearbyPairingClient.checkCode(CERT.toUpperCase(), CLIENT.toUpperCase(), NONCE));
        assertNotEquals("3120", NearbyPairingClient.checkCode(CERT.replace('a', 'b'), CLIENT, NONCE));
    }

    @Test
    public void nonceIsThirtyTwoLowercaseHex() {
        String nonce = NearbyPairingClient.newNonce();
        assertTrue(nonce, nonce.matches("[0-9a-f]{32}"));
        assertNotEquals(nonce, NearbyPairingClient.newNonce());
    }
}
