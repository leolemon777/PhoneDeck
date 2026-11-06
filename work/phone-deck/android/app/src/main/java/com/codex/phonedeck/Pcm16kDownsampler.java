package com.codex.phonedeck;

/**
 * 48 kHz → 16 kHz 的 3 倍抽取（Kaiser 窗 sinc 抗混叠低通，144 抽头，截止约 7.4 kHz）。
 * Wi-Fi 共享供音给声明 sharedPcm16kV1 的电脑时使用：流量降为 1/3，语音识别本身只用 8 kHz 以内。
 * 有状态，跨帧连续；每个采集循环一个实例，只在采集线程调用。
 */
final class Pcm16kDownsampler {
    static final int FACTOR = 3;
    static final int TAPS = 144;
    static final int OUTPUT_RATE = 16_000;
    private static final float[] COEFFICIENTS = design();

    /// 双倍长度环形缓冲：任意时刻 [position, position + TAPS) 是从旧到新的最近 TAPS 个输入样本。
    private final float[] history = new float[TAPS * 2];
    private int position;
    private int phase;

    /** 输入 48 kHz 小端 PCM16 的前 count 字节（偶数），返回 16 kHz 小端 PCM16。 */
    byte[] process(byte[] pcm48, int count) {
        int samples = count / 2;
        byte[] output = new byte[((samples + phase) / FACTOR + 1) * 2];
        int written = 0;
        for (int index = 0; index + 1 < count; index += 2) {
            float sample = (short) ((pcm48[index] & 0xff) | (pcm48[index + 1] << 8));
            history[position] = sample;
            history[position + TAPS] = sample;
            position = (position + 1) % TAPS;
            phase++;
            if (phase == FACTOR) {
                phase = 0;
                float sum = 0f;
                // 系数对称（线性相位），方向无关。
                for (int tap = 0; tap < TAPS; tap++) {
                    sum += COEFFICIENTS[tap] * history[position + tap];
                }
                int value = Math.round(sum);
                if (value > Short.MAX_VALUE) {
                    value = Short.MAX_VALUE;
                } else if (value < Short.MIN_VALUE) {
                    value = Short.MIN_VALUE;
                }
                output[written++] = (byte) value;
                output[written++] = (byte) (value >> 8);
            }
        }
        return written == output.length ? output : java.util.Arrays.copyOf(output, written);
    }

    private static float[] design() {
        double[] prototype = kaiserLowPass(TAPS, 7_400.0 / 48_000.0, 7.0);
        double sum = 0;
        for (double value : prototype) {
            sum += value;
        }
        float[] result = new float[TAPS];
        for (int index = 0; index < TAPS; index++) {
            result[index] = (float) (prototype[index] / sum);
        }
        return result;
    }

    /** Kaiser 窗 sinc 低通；cutoff 为相对采样率的截止频率（0–0.5）。与接收端 Pcm16kTo48kStream 同一设计。 */
    static double[] kaiserLowPass(int taps, double cutoff, double beta) {
        double[] result = new double[taps];
        double center = (taps - 1) / 2.0;
        double denominator = besselI0(beta);
        for (int n = 0; n < taps; n++) {
            double x = n - center;
            double sinc = x == 0 ? 2 * cutoff : Math.sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double ratio = 2.0 * n / (taps - 1) - 1;
            double window = besselI0(beta * Math.sqrt(Math.max(0, 1 - ratio * ratio))) / denominator;
            result[n] = sinc * window;
        }
        return result;
    }

    private static double besselI0(double value) {
        double sum = 1;
        double term = 1;
        double half = value / 2;
        for (int k = 1; k < 64; k++) {
            term *= (half / k) * (half / k);
            sum += term;
            if (term < sum * 1e-12) {
                break;
            }
        }
        return sum;
    }
}
