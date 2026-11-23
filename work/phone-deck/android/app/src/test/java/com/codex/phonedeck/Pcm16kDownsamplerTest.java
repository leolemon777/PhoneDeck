package com.codex.phonedeck;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import java.io.ByteArrayOutputStream;
import java.util.Arrays;
import java.util.Collections;

import org.junit.Test;

/** Wi-Fi 共享 16 kHz 上传：48 → 16 kHz 抽取质量、跨帧连续性与能力协商。 */
public final class Pcm16kDownsamplerTest {
    private static final int FRAME_BYTES = 1_920; // 20 ms @ 48 kHz

    @Test
    public void twentyMillisecondFrameBecomesOneThirdTheBytes() {
        Pcm16kDownsampler downsampler = new Pcm16kDownsampler();
        for (int frame = 0; frame < 5; frame++) {
            assertEquals(640, downsampler.process(new byte[FRAME_BYTES], FRAME_BYTES).length);
        }
    }

    @Test
    public void speechBandToneKeepsAmplitude() {
        short[] output = run(tone(1_000, 10_000, 48_000));
        double amplitude = amplitudeAt(output, 1_000, 16_000, 600);
        assertEquals("1 kHz 幅度应保持（±1.5%）", 10_000, amplitude, 150);
    }

    @Test
    public void toneAboveEightKilohertzIsRemovedNotAliased() {
        // 12 kHz 若不滤波会混叠成 4 kHz，直接进入语音频带。
        short[] output = run(tone(12_000, 10_000, 48_000));
        double aliased = amplitudeAt(output, 4_000, 16_000, 600);
        assertTrue("混叠残留应低于 -50 dB：" + aliased, aliased < 10_000 * 0.003);
    }

    @Test
    public void splittingIntoFramesMatchesOneBlock() {
        byte[] pcm = pcm(tone(700, 8_000, 48_000));
        byte[] whole = new Pcm16kDownsampler().process(pcm, pcm.length);

        Pcm16kDownsampler framed = new Pcm16kDownsampler();
        ByteArrayOutputStream joined = new ByteArrayOutputStream();
        // 故意用不是 3 的倍数的样本数切分，检验相位跨帧延续。
        int offset = 0;
        int[] sizes = {1_918, 1_922, 1_920, 2};
        int index = 0;
        while (offset < pcm.length) {
            int size = Math.min(sizes[index++ % sizes.length], pcm.length - offset);
            byte[] part = Arrays.copyOfRange(pcm, offset, offset + size);
            byte[] out = framed.process(part, part.length);
            joined.write(out, 0, out.length);
            offset += size;
        }
        assertArrayEquals(whole, joined.toByteArray());
    }

    @Test
    public void onlyReceiversAdvertisingTheCapabilityGetSixteenKilohertz() {
        assertEquals(16_000, SharedAudioPolicies.wifiUploadRate(
                Arrays.asList("sharedMicrophone", "sharedPcm16kV1")));
        assertEquals(48_000, SharedAudioPolicies.wifiUploadRate(
                Collections.singletonList("sharedMicrophone")));
        assertEquals(48_000, SharedAudioPolicies.wifiUploadRate(null));
    }

    private static short[] tone(double frequency, double amplitude, int rate) {
        short[] samples = new short[rate];
        for (int n = 0; n < samples.length; n++) {
            samples[n] = (short) Math.round(amplitude * Math.sin(2 * Math.PI * frequency * n / rate));
        }
        return samples;
    }

    private static short[] run(short[] input) {
        Pcm16kDownsampler downsampler = new Pcm16kDownsampler();
        byte[] pcm = pcm(input);
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        for (int offset = 0; offset < pcm.length; offset += FRAME_BYTES) {
            int size = Math.min(FRAME_BYTES, pcm.length - offset);
            byte[] out = downsampler.process(Arrays.copyOfRange(pcm, offset, offset + size), size);
            output.write(out, 0, out.length);
        }
        byte[] bytes = output.toByteArray();
        short[] samples = new short[bytes.length / 2];
        for (int index = 0; index < samples.length; index++) {
            samples[index] = (short) ((bytes[index * 2] & 0xff) | (bytes[index * 2 + 1] << 8));
        }
        return samples;
    }

    private static double amplitudeAt(short[] samples, double frequency, int rate, int skip) {
        double i = 0;
        double q = 0;
        int length = rate / 2;
        for (int n = skip; n < skip + length; n++) {
            double angle = 2 * Math.PI * frequency * n / rate;
            i += samples[n] * Math.cos(angle);
            q += samples[n] * Math.sin(angle);
        }
        return 2 * Math.sqrt(i * i + q * q) / length;
    }

    private static byte[] pcm(short[] samples) {
        byte[] bytes = new byte[samples.length * 2];
        for (int index = 0; index < samples.length; index++) {
            bytes[index * 2] = (byte) samples[index];
            bytes[index * 2 + 1] = (byte) (samples[index] >> 8);
        }
        return bytes;
    }
}
