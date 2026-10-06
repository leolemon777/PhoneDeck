package com.codex.phonedeck;

import org.junit.Test;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

import static org.junit.Assert.*;

/**
 * M0-B 跨语言契约消费：contracts/ 的同一批样本驱动 Android 客户端侧的契约不变量。
 * 覆盖：全部样本可解析；合法请求样本满足 v2 信封不变量（protocolVersion=2、
 * requestId 非空且 ≤128、sessionId 可解析为 UUID、targetComputerId 非空，
 * 无 protocolVersion 的按遗留路径只要求 requestId）；动作拼写与客户端常量一致；
 * 组合键边界（1–4 键、无重复、holdMs 20–500）对合法样本成立、对越界样本拒绝。
 * 样本路径由测试向上查找仓库根的 contracts/ 目录；任何样本增删都直接改变本测试输入。
 * 单元测试环境没有 android.jar 的 org.json 实现，故内置最小 JSON 解析器。
 */
public final class ContractsConformanceTest {

    private static File contractsRoot() {
        File dir = new File(System.getProperty("user.dir")).getAbsoluteFile();
        for (int depth = 0; depth < 10 && dir != null; depth++, dir = dir.getParentFile()) {
            File candidate = new File(dir, "contracts");
            if (new File(candidate, "samples/valid").isDirectory()) {
                return candidate;
            }
        }
        throw new AssertionError("contracts 目录未找到，user.dir="
                + System.getProperty("user.dir"));
    }

    private static List<File> sampleFiles(String folder) {
        File[] files = new File(contractsRoot(), "samples/" + folder).listFiles(
                (dir, name) -> name.endsWith(".json"));
        assertNotNull("样本目录不存在：" + folder, files);
        return Arrays.asList(files);
    }

    private static Map<String, Object> parseSample(File file) throws Exception {
        String content = new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8);
        Object parsed = MiniJson.parse(content);
        assertTrue(file.getName() + " 应为 JSON 对象", parsed instanceof Map);
        return asMap(parsed);
    }

    private static boolean isRequestSample(String name) {
        return name.startsWith("request-confirmation-input-")
                || name.startsWith("request-confirmation-dictation-");
    }

    @Test
    public void allContractSamplesParse() throws Exception {
        List<File> valid = sampleFiles("valid");
        List<File> invalid = sampleFiles("invalid");
        assertTrue("valid 样本数量异常：" + valid.size(), valid.size() >= 20);
        assertTrue("invalid 样本数量异常：" + invalid.size(), invalid.size() >= 30);
        for (File file : valid) {
            parseSample(file);
        }
        for (File file : invalid) {
            parseSample(file);
        }
    }

    @Test
    public void validRequestSamplesSatisfyEnvelopeInvariants() throws Exception {
        int checked = 0;
        for (File file : sampleFiles("valid")) {
            if (!isRequestSample(file.getName())) {
                continue;
            }
            checked += 1;
            Map<String, Object> sample = parseSample(file);
            Object protocolVersion = sample.get("protocolVersion");
            String requestId = asString(sample.get("requestId"));
            if (protocolVersion == null) {
                // 遗留路径（如 paste）：只要求 requestId。
                assertTrue(file.getName() + " 遗留请求应有 requestId",
                        requestId != null && !requestId.trim().isEmpty());
                continue;
            }
            assertEquals(file.getName() + " 协议版本应为 2", 2L, asLong(protocolVersion));
            assertTrue(file.getName() + " requestId 非空且 ≤128",
                    requestId != null && !requestId.trim().isEmpty()
                            && requestId.trim().length() <= 128);
            String sessionId = asString(sample.get("sessionId"));
            UUID.fromString(sessionId);
            String target = asString(sample.get("targetComputerId"));
            assertTrue(file.getName() + " targetComputerId 非空",
                    target != null && !target.trim().isEmpty());
        }
        assertTrue("请求样本数量异常：" + checked, checked >= 6);
    }

    @Test
    public void actionSpellingsMatchClientConstants() throws Exception {
        Set<String> knownActions = new HashSet<>(Arrays.asList(
                ShortcutButtonConfig.ACTION_KEY_CHORD,
                ShortcutButtonConfig.ACTION_TEXT,
                ShortcutButtonConfig.ACTION_MACRO,
                "paste", "dictation"));
        // 客户端常量必须与契约样本的拼写逐字一致。
        assertEquals("keyChord", ShortcutButtonConfig.ACTION_KEY_CHORD);
        assertEquals("text", ShortcutButtonConfig.ACTION_TEXT);
        assertEquals("macro", ShortcutButtonConfig.ACTION_MACRO);
        for (File file : sampleFiles("valid")) {
            if (!file.getName().startsWith("request-confirmation-input-")) {
                continue;
            }
            Map<String, Object> sample = parseSample(file);
            String action = asString(sample.get("action"));
            assertTrue(file.getName() + " 动作不在已知集合：" + action,
                    knownActions.contains(action));
        }
    }

    /** 契约不变量：组合键 1–4 个、无重复、holdMs 默认 45 且在 20–500。 */
    private static void checkChord(List<?> keys, Object holdMs, String context) {
        assertNotNull(context + " 缺少 keys", keys);
        assertTrue(context + " 键数应在 1–4", keys.size() >= 1 && keys.size() <= 4);
        Set<Object> distinct = new HashSet<>(keys);
        assertEquals(context + " 键不得重复", distinct.size(), keys.size());
        long hold = holdMs == null ? 45L : asLong(holdMs);
        assertTrue(context + " holdMs 应在 20–500：" + hold, hold >= 20 && hold <= 500);
    }

    @Test
    public void chordInvariantsAcceptValidAndRejectInvalidSamples() throws Exception {
        for (File file : sampleFiles("valid")) {
            String name = file.getName();
            if (!name.startsWith("request-confirmation-input-keychord")) {
                continue;
            }
            Map<String, Object> sample = parseSample(file);
            checkChord(asList(sample.get("keys")), sample.get("holdMs"), name);
        }
        String[] rejected = {
                "input-keychord-five-keys.json",
                "input-keychord-zero-keys.json",
                "input-keychord-missing-keys.json",
                "input-keychord-holdms-above-range.json",
                "input-keychord-holdms-below-range.json",
                "input-keychord-duplicate-keys.json",
        };
        for (String name : rejected) {
            Map<String, Object> sample = parseSample(
                    new File(contractsRoot(), "samples/invalid/" + name));
            boolean threw = false;
            try {
                checkChord(asList(sample.get("keys")), sample.get("holdMs"), name);
            } catch (AssertionError expected) {
                threw = true;
            }
            assertTrue(name + " 应被组合键不变量拒绝", threw);
        }
    }

    private static Map<String, Object> asMap(Object value) {
        @SuppressWarnings("unchecked")
        Map<String, Object> map = (Map<String, Object>) value;
        return map;
    }

    private static List<Object> asList(Object value) {
        if (value == null) {
            return null;
        }
        assertTrue("应为数组", value instanceof List);
        @SuppressWarnings("unchecked")
        List<Object> list = (List<Object>) value;
        return list;
    }

    private static String asString(Object value) {
        return value instanceof String ? (String) value : null;
    }

    private static long asLong(Object value) {
        return ((Number) value).longValue();
    }

    /** 最小 JSON 解析器：对象→LinkedHashMap，数组→ArrayList，整数→Long，小数→Double。 */
    static final class MiniJson {
        private final String src;
        private int pos;

        private MiniJson(String src) {
            this.src = src;
        }

        static Object parse(String source) {
            MiniJson parser = new MiniJson(source);
            parser.skipWhitespace();
            Object value = parser.readValue();
            parser.skipWhitespace();
            if (parser.pos != parser.src.length()) {
                throw parser.error("存在尾随内容");
            }
            return value;
        }

        private AssertionError error(String message) {
            return new AssertionError("JSON 解析失败@" + pos + "：" + message);
        }

        private void skipWhitespace() {
            while (pos < src.length()
                    && Character.isWhitespace(src.charAt(pos))) {
                pos++;
            }
        }

        private char peek() {
            if (pos >= src.length()) {
                throw error("意外结束");
            }
            return src.charAt(pos);
        }

        private Object readValue() {
            char c = peek();
            switch (c) {
                case '{':
                    return readObject();
                case '[':
                    return readArray();
                case '"':
                    return readString();
                case 't':
                    expect("true");
                    return Boolean.TRUE;
                case 'f':
                    expect("false");
                    return Boolean.FALSE;
                case 'n':
                    expect("null");
                    return null;
                default:
                    return readNumber();
            }
        }

        private void expect(String word) {
            if (!src.startsWith(word, pos)) {
                throw error("期望 " + word);
            }
            pos += word.length();
        }

        private Map<String, Object> readObject() {
            pos++;
            Map<String, Object> map = new LinkedHashMap<>();
            skipWhitespace();
            if (peek() == '}') {
                pos++;
                return map;
            }
            while (true) {
                skipWhitespace();
                String key = readString();
                skipWhitespace();
                if (peek() != ':') {
                    throw error("期望 :");
                }
                pos++;
                skipWhitespace();
                map.put(key, readValue());
                skipWhitespace();
                char c = peek();
                if (c == ',') {
                    pos++;
                    continue;
                }
                if (c == '}') {
                    pos++;
                    return map;
                }
                throw error("期望 , 或 }");
            }
        }

        private List<Object> readArray() {
            pos++;
            List<Object> list = new ArrayList<>();
            skipWhitespace();
            if (peek() == ']') {
                pos++;
                return list;
            }
            while (true) {
                skipWhitespace();
                list.add(readValue());
                skipWhitespace();
                char c = peek();
                if (c == ',') {
                    pos++;
                    continue;
                }
                if (c == ']') {
                    pos++;
                    return list;
                }
                throw error("期望 , 或 ]");
            }
        }

        private String readString() {
            if (peek() != '"') {
                throw error("期望字符串");
            }
            pos++;
            StringBuilder builder = new StringBuilder();
            while (true) {
                char c = peek();
                if (c == '"') {
                    pos++;
                    return builder.toString();
                }
                if (c == '\\') {
                    pos++;
                    char escape = peek();
                    pos++;
                    switch (escape) {
                        case '"': builder.append('"'); break;
                        case '\\': builder.append('\\'); break;
                        case '/': builder.append('/'); break;
                        case 'b': builder.append('\b'); break;
                        case 'f': builder.append('\f'); break;
                        case 'n': builder.append('\n'); break;
                        case 'r': builder.append('\r'); break;
                        case 't': builder.append('\t'); break;
                        case 'u':
                            if (pos + 4 > src.length()) {
                                throw error("\\u 转义不完整");
                            }
                            String hex = src.substring(pos, pos + 4);
                            builder.append((char) Integer.parseInt(hex, 16));
                            pos += 4;
                            break;
                        default:
                            throw error("未知转义 \\" + escape);
                    }
                    continue;
                }
                if (c < 0x20) {
                    throw error("字符串含控制字符");
                }
                builder.append(c);
                pos++;
            }
        }

        private Object readNumber() {
            int start = pos;
            if (peek() == '-') {
                pos++;
            }
            boolean fractional = false;
            while (pos < src.length()) {
                char c = src.charAt(pos);
                if (c >= '0' && c <= '9') {
                    pos++;
                } else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') {
                    fractional = true;
                    pos++;
                } else {
                    break;
                }
            }
            String token = src.substring(start, pos);
            if (token.isEmpty() || token.equals("-")) {
                throw error("非法数字");
            }
            return fractional ? (Object) Double.parseDouble(token) : (Object) Long.parseLong(token);
        }
    }
}
