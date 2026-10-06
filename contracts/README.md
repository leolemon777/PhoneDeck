# PhoneDeck 契约测试台（contracts/）

按《开源可用性、全端兼容与发布验收规格》[docs/release/OPEN_SOURCE_RELEASE_SPEC.md](../docs/release/OPEN_SOURCE_RELEASE_SPEC.md)
§16.1 建立：**先固定行为，再提取公共核心**。本目录只保存契约（JSON Schema、正/反样本、
错误映射、状态转移、参数策略），四端（Android Java / Windows C# / macOS C# / 未来 iOS Swift）
消费**同一批**夹具，等价行为测试通过后再考虑提取 Receiver.Core。本目录不包含任何产品实现，
也不修改 contracts/ 之外的任何文件。

规格 §16.1 原文确认本目录“尚未创建”，本目录即为该提案的落地；其中标注 **[目标]** 的字段
是契约目标设计（规格 §1：新增契约字段、配对机制及状态机均为目标设计，不得冒充现有
protocol v2 的事实），标注 **[当前]** 的约束已由源码证实并附路径，标注 **[候选]** 的数值
为规格建议值（评审冻结前不得当成已实现）。

## 目录结构

```text
contracts/
  README.md                 本文件：结构、跨语言消费方式、规格映射
  error-codes.json          §8 十类错误（+请求格式类）→ HTTP/产品错误码映射 [当前/target 逐条标注]
  session-states.json       五态状态机 + §16.3 七条竞态规则 + 重连窗口规则(RC1/RC2) + 终止墓碑契约
  pairing-and-identity.json §16.2 第 1/2 行：发现与配对、身份与授权的参数与生命周期契约
  request-dedup.json        §16.2 第 3 行剩余项：requestId 作用域/载荷哈希/去重缓存/查询重试策略
  capability-policy.json    §16.2 第 5 行剩余项：版本事实/维护窗口/协议窗口/permissionDenied/降级矩阵
  audio-budgets.json        §16.2 第 6 行剩余项：采样率转换 + 队列/尾音/恢复窗口数值预算
  config-policy.json        §16.2 第 7 行剩余项：原子保存/崩溃恢复/迁移备份/导出秘密隔离
  schemas/                  JSON Schema（draft-07，$id 基址 https://phonedeck.dev/contracts/schemas/）
    definitions.schema.json               共用字段定义（信封/长度/枚举/占位符规则）
    request.input.schema.json             POST /api/input 请求体（含 keyChord/macro 与遗留动作）
    request.dictation.schema.json         POST /api/dictation/start|stop 请求体
    request.audio-stream.schema.json      POST /api/audio/stream 建流头与 PCM 基线描述
    request.config-desktop.schema.json    POST /api/config/desktop/{voice,connection} 写入
    phone-managed-settings.schema.json    phoneManagedSettingsV1 手机侧托管设置缓存 [目标 schema 字段]
    response.ok.schema.json               三类成功 ACK
    response.error.schema.json            错误响应 + 注册错误码枚举（与 error-codes.json 强一致）
    health.schema.json                    GET /api/health 能力协商文档
    session-state.schema.json             会话状态快照
    pairing.schema.json                   §16.2 第 1 行：UDP 发现应答/USB 配对响应 [当前] + 二维码载荷 [目标]
    identity.schema.json                  §16.2 第 2 行：逐手机身份/凭据记录 + 旧共享令牌迁移计划 [目标]
    session-tombstone.schema.json         §16.2 第 4 行：终止墓碑记录 [目标结构]
    config-export.schema.json             §16.2 第 7 行：普通导出文档（秘密隔离）[目标形态]
  samples/
    valid/                   31 个合法样本 + manifest.json（file→schema→域→覆盖点）
    invalid/                 47 个非法样本 + manifest.json（每条注明 violates + specRef + 边界族）
  tools/
    validate.py              校验脚本，仅 Python 标准库；退出码 0=通过 / 非 0=失败
```

## 运行校验

```bash
# 方式一：从仓库根目录（相对路径仅在此有效）
python contracts/tools/validate.py

# 方式二：先进入 contracts 目录
cd contracts
python tools/validate.py

# 方式三：从任意目录用绝对路径（脚本按自身位置定位 contracts/，不依赖当前工作目录）
python "<仓库绝对路径>/contracts/tools/validate.py"

# 显式指定根目录
python tools/validate.py --root <contracts 目录>
```

**退出码与排错**：本脚本只产生 0（全部通过）/ 1（存在失败）。
若看到 `exit=2` 且报 `can't open file ... [Errno 2] No such file or directory`，
是 Python 没找到**脚本文件本身**——相对路径 `contracts/tools/validate.py`
按调用时的工作目录解析（例如在 `work/phone-deck/android/...` 下调用会拼出不存在的
路径），校验并未运行；请改用仓库根目录作为工作目录或绝对路径重试，不要据此判定契约失败。

检查项（见 `tools/validate.py` 文档串）：C1 全部 JSON 可解析；C2 合法样本通过 schema；
C3 非法样本确实被拒绝且每条注明违反条款；C4 error-codes.json 内部一致并与
`response.error.schema.json` 枚举完全一致；C5 session-states.json 五态/转移/R1–R7+RC1–RC2
轨迹逐条模拟执行、墓碑 reasons 与 schema 枚举交叉一致；C6 样本占位符卫生（token/secret 类
字段必须以 `PLACEHOLDER` 开头）；C7 五份策略契约文件内部一致（必填项/数值/状态标注/注册码
引用 + 降级矩阵↔能力枚举、凭据作用域↔identity schema、导出样本全树秘密字段扫描）。
当前基线：101 个 JSON 全部解析，31 合法 / 47 非法样本覆盖 8 个域（请求确认/会话/能力/音频/
配置/错误/配对/身份），24 条转移、7+2 条规则、16 条轨迹全部模拟通过。

## Java / C# / Swift 共用消费方式

三个平台读**同一份** `schemas/` 与 `samples/`，禁止各自复制出第二套契约（规格 §16.1）。
推荐接入点（库为建议，验收以实际跑通为准——本轮未在四端接入，属后续任务 M0-B）：

| 端 | 消费方式 | 建议挂载点 |
|---|---|---|
| Windows/macOS（.NET 8） | 用 `Newtonsoft.Json.Schema`（JsonSchema.Parse）或 `JsonSchema.Net` 加载 `schemas/*.schema.json`，遍历 `samples/valid|invalid/manifest.json` 逐一断言 | `PhoneDeck.Server.Tests` / `PhoneDeck.Receiver.Tests` 新增契约测试类；`$ref` 按相对文件解析（schemas/ 同目录） |
| Android（Java 17） | 用 `com.github.everit-org.json-schema:json-schema` 或 `com.networknt:json-schema-validator` 加载同一批文件；无本地 JSON Schema 库时，至少断言非法样本产生的错误类别与 `error-codes.json` 一致 | `work/phone-deck/android/app/src/test/`（JUnit）；Gradle 需允许读取仓库相对路径（`../contracts`） |
| iOS（Swift，未来） | `JSONSchema`（SwiftPM）或通过轻量桥接复用 C# 校验结果；契约文件在仓库内随版本走 | iOS 客户端单测 target |

跨语言实现注意事项：

- **$ref 解析**：schema 间互引使用兄弟文件相对引用（`definitions.schema.json#/definitions/x`）。
  `tools/validate.py` 自带解析器；标准库用户应把 `$id` 基址
  `https://phonedeck.dev/contracts/schemas/` 映射到本目录或直接用相对路径加载。
- **长度单位**：`maxLength`/`minLength` 按 JSON Schema 语义数 **Unicode 码点**；当前 C#
  接收端按 **UTF-16 码元**计数（`InputCommandProcessor.cs:11`）。边界样本统一用 BMP 字符
  使两种单位一致（规格 §8“必须明确跨语言长度单位”）；含 emoji 的文本在接收端会先于 4096
  码点触发上限，这是已记录的实现差异，不是契约矛盾。
- **格式注解**：schema 不依赖 `format` 关键字（GUID 等用显式 `pattern`），避免各库 format
  校验开关差异。

## 与规格章节的映射（§16.2 契约表逐行）

| 规格章节 | 要求摘要 | 本目录落点 |
|---|---|---|
| §16.2 第 1 行 发现与配对 | 服务名/端口、二维码版本/大小、证书绑定、一次性材料长度/有效期、错误/限速、手工替代方案 | `pairing.schema.json`（发现应答/USB 配对/QR 载荷）+ `pairing-and-identity.json`（UDP 8767/魔术串/服务名 [当前]；QR 版本/大小/材料 32 字符·128bit/120s/5 次 [候选，D02]；错误/限速映射；手工替代方案）+ 样本 pairing-* |
| §16.2 第 2 行 身份与授权 | computerId/clientId/pairingId、凭据作用域、存储、轮换、撤销、旧共享令牌迁移 | `identity.schema.json`（身份记录/凭据作用域/迁移计划）+ `pairing-and-identity.json`（存储 [当前 base64 随机令牌]/轮换/撤销 ≤1s [候选]/迁移窗口）+ 样本 identity-* |
| §8 PRO-02 / §16.2 第 3 行 请求确认 | requestId 作用域、规范化载荷哈希、去重 TTL/容量、并发互斥、ACK 语义、响应丢失后的查询/重试 | `request.input.schema.json`（信封必填）+ `request-dedup.json`（clientId 分区/SHA256 哈希 [目标]/TTL 30s [当前]/禁驱逐近期/崩溃不补发/结果未知不重放）+ invalid 样本 |
| §8 PRO-03 / §16.2 第 4 行 会话 | sessionId、所有者、代次、租约、启停/取消顺序、终止墓碑、重连窗口、未知状态与过期处理 | `session-state.schema.json` + `session-states.json`（转移表/R1–R7）+ `session-tombstone.schema.json` + tombstoneContract + 重连规则 RC1/RC2（15s 窗口 [候选]）|
| §16.2 第 5 行 能力 | supported/available/busy/permissionDenied 分开；协商失败的降级；每端最低版本与旧协议窗口 | `health.schema.json`（能力枚举 + audio.available/streaming/stale、capturing 三态、permissionDenied [目标]）+ `capability-policy.json`（版本事实/维护窗口 D10/协议窗口/降级矩阵全 10 项）+ 样本 capability-* |
| §16.2 第 6 行 音频 | 字节序/PCM 格式/帧长/EOF、实际采样率转换、队列/尾音上限与流冲突 | `request.audio-stream.schema.json`（pcm-s16le/48k/mono/20ms/deviceSampleRateHz；EOF=HTTP 体结束；冲突→409）+ `audio-budgets.json`（120ms 共享队列/400ms 静音保护/1500ms 服务端 pre-roll/3000ms 排空/4900ms 停止等待 [当前]；200/500ms 停采、3s/5s 收尾、15s 恢复窗口 [候选]）|
| §16.2 第 7 行 配置 | schema/revision、分组写入、热应用互斥、原子保存、迁移/备份/导出秘密隔离 | `request.config-desktop.schema.json`、`phone-managed-settings.schema.json`、`config-export.schema.json` + `config-policy.json`（WriteAtomic 步骤 [当前]/V59 崩溃恢复期望值夹具/未来 schema 拒绝/损坏备份/导出禁止字段全树扫描）+ session-states T15–T17（R6）|
| §16.2 第 8 行 错误与诊断 | 稳定错误码、HTTP 映射、可重试性、中英文文案、脱敏、时钟与快照新鲜度 | `error-codes.json`（11 类齐全）+ `response.error.schema.json` + health/session-state 的 checkedAtMs/ageMs/stale/probeStale/lease |
| §16.3 规则 1–7 | 七条会话竞态规则 | `session-states.json` raceRules R1–R7 + 16 条可执行 traces（validate.py 逐条模拟终态断言）|
| §12 SEC-01/03/04/05 | 无任意 shell/脚本、凭据不入库、样本脱敏 | 动作/宏步骤/凭据作用域白名单（runShell/shellCommand/shell 均为非法样本）、C6 占位符卫生、config-policy 导出秘密隔离扫描、definitions.placeholderToken |
| §12 SEC-02 | LAN 鉴权头、未授权拒绝、发现不泄露秘密 | `request.audio-stream.schema.json` X-PhoneDeck-Token 必填（PLACEHOLDER）、error-codes unauthorized(401)、发现应答 schema 禁令牌字段 + 非法样本 pairing-discovery-token-leak |

## 形态层与语义层的边界（重要）

JSON Schema 只能拒绝**形态层**违例。以下规则**无法**用 draft-07 表达，由
`session-states.json`（可执行轨迹）与接收端语义校验共同承担，跨语言实现不得省略：

| 语义规则 | 表达位置 |
|---|---|
| 组合键“恰好 1 个普通键”（schema 只表达“至少 1 个非修饰键”） | 接收端 `Program.cs` ParseKeyChord:987-991；README 记录 |
| targetComputerId **值**不等于本机 computerId | 接收端 `TargetEnvelopeValidator.cs:46-50` → wrong_target/PD-ERR-400-02 |
| 同 requestId 不同载荷拒绝（哈希比对） | `request-dedup.json` canonicalPayloadHash [目标] + 接收端执行记录 |
| stop 引用非当前活动会话（过期会话） | `DictationSessionManager.cs:158-162` → expired_session/PD-ERR-409-03；session-states T11/T12/T20 |
| 未知引擎模式/未知引擎 id | 接收端按引擎档案（JSON，AGENTS：新增引擎走档案不改代码） |
| start/stop 竞态、夺权、互斥、重启清理、重连资格 | session-states.json R1–R7 + RC1/RC2（模拟执行）+ 墓碑契约 |
| 引擎采集三态（null=探针未知）不是布尔 | `health.schema.json`/`session-state.schema.json` capturing: ["boolean","null"] |
| 配额/限速的运行时计数（429 等） | `pairing-and-identity.json` pairingErrors [目标]；运行时行为由 L2 验证（V11） |

## 当前事实 vs 契约目标 vs 契约收紧

- **[当前]** 信封字段与上限、动作白名单、能力枚举、401/400/409/503/499/500 行为、
  revision 校验、原子写入、ConfigurationGate 互斥、UDP 发现/USB 配对形态、音频数值常量、
  去重 TTL 30s——均附源码路径（见各 schema 的 description 与策略文件的 grounding）。
- **[目标]** 错误响应中的 `code` 字段（现源码仅输出 `{ok,error}`）、错误码 `PD-ERR-*`、
  会话 `generation/ownerClientId/lease`、`phoneManagedSettings` 缓存文档的
  `schema/schemaVersion`、timeout(504) 与 input_permission_denied(403) 映射、逐手机凭据/
  QR 配对/墓碑/permissionDenied/导出文档——冻结于规格 §22 D02/D03；实现前不得宣称已生效。
- **[候选]** 配对 120s/5 次、材料 ≥32 字符·≥128bit、QR ≤512 字节、停采 200/500ms、
  收尾 3s/5s、恢复窗口 15s、撤销 ≤1s、墓碑保留 30s、维护窗口策略——均为规格建议值，
  D02/D03/D04/D10 冻结，冻结前不得当成已实现门槛。
- **[契约收紧]** 两处**有意**比当前源码更严并已记录：① `definitions.sessionId` 把 GUID 钉死
  为 dashed 规范形式（.NET Guid.TryParse 实际更宽）；② `request.audio-stream.schema.json`
  的 `X-PhoneDeck-Protocol` 钉为 `^[1-2]$`（当前接收端仅拒绝 >2，0/负数值按遗留放行，
  Program.cs:416-423 + TargetEnvelopeValidator.cs:29-51）。收紧由非法样本
  `audio-stream-protocol-header-zero.json` 固化；是否放宽至与源码一致由 D03 评审决定。
