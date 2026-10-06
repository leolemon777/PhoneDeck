# M1-A 配对与授权安全设计（D02 方向落地）

> 版本 1.0（独立评审修订版）· 日期 2026-09-29 · 依据：用户已确认 D02 方向"成熟加密实现 + 本机确认 + 逐手机凭据"。
> 上游约束：[开源发布规格](../release/OPEN_SOURCE_RELEASE_SPEC.md) §7（NET-01…NET-08）、§12（SEC-02/03/05）、§16.2 前两行与候选参数注（安全评审前不自创密码协议）；契约夹具 `contracts/pairing-and-identity.json`、`schemas/pairing.schema.json`、`schemas/identity.schema.json`。
> 本文档只做设计；§12 评审记录含阻断项处置，按第 9 节切分实现。

## 1. 现状基线（全部已核源码）

| 环节 | 现状 | 位置 |
|---|---|---|
| 发现 | UDP 8767 广播应答，不含令牌/证书指纹 | `LanDiscoveryResponder.cs:8-40` |
| 首配 | `POST /api/lan/pair`，仅 USB 回环来源可达否则 404；返回共享 accessToken + 证书 SHA256 + 地址表 | `Program.cs:381-401` |
| 通道 | HTTPS 8766，RSA-2048 自签 `CN=PhoneDeck-{computerId}`，手机钉扎指纹 | `LanIdentity.cs:9,120-133` |
| 业务鉴权 | 单一共享令牌 `X-PhoneDeck-Token`（随机字节） | `LanIdentity.cs:106-116`、`LanRequestAuthenticator.cs` |
| 手机存储 | SharedPreferences 明文：Device{computerId, lanToken, certificateSha256, …} | `TargetDeviceManager.java:14-57` |
| 已知缺口（评审核实） | 全仓库无 Origin/Host 校验（V13/SEC-02 相关，另立任务） | `Program.cs` 鉴权中间件 |

## 2. 目标身份与凭据模型

- **computerId**：接收端稳定身份（现状不变）；IP/名称/序号不充当身份（DEV-01）。
- **clientId**：每手机每配对一次签发（GUID v4），撤销、限速、去重缓存（clientId 分区）均按它生效。
- **pairingId**：每次配对尝试的 GUID，出现在 QR 载荷与配对请求中，凭据可追溯签发来源，支持"撤销该次配对签发的所有凭据"。
- **凭据形态（决策 A，评审无异议）**：clientId 绑定一条高熵随机 bearer 令牌（≥192 bit，`RandomNumberGenerator`），经 TLS+证书钉扎信道传输与使用。**不自创密码协议**：机密性由钉扎 TLS 提供，一次性材料只作"配对窗口持有证明"。备选 B（Android Keystore EC P-256 请求签名）为升级候选，不在本里程碑。
- **服务端只存令牌哈希（SHA-256，无盐，≥192bit 熵下抗枚举）**；令牌本体只存在于签发响应与手机端。凭据丢失/找回唯一路径 = 重新扫码配对（G-2 处置）。
- **作用域**：`control` / `audio` / `settings` / `update-request`。M1-A 签发默认全授予并显式记录；按作用域部分授权/撤销为后续能力，本次只做整体撤销。授予清单在手机配对页与本机确认卡展示（不进 QR 载荷，见 §3）。
- **"已有输入权限 ≠ 可替换信任根"**：任何已认证请求（含 settings 作用域）不得触发证书替换、配对材料重发或凭据签发；信任根变更只走本机配对窗口。
- **证书轮换代价（有意选择，评审 M-5 相关）**：凭据绑定 certSha256，证书轮换=全员重新配对；与契约"经已认证通道或重新本机配对"相容，5 年到期/重装时接受该代价。DPAPI 使克隆机的 clients.json 鉴权表不可解密（data 克隆的净收益，见 §6 末）。

## 3. QR 配对流程（NET-02）

```
电脑（托盘开启配对窗口）                    手机（扫码 / 手工输入）
1. 生成 oneTimeMaterial（≥32 字符，≥128bit）
   生成 pairingId(GUID)
2. 展示 QR：{version:1, computerId, displayName,
   httpsPort:8766, certificateSha256,
   pairingId, oneTimeMaterial,
   validSeconds, maxFailuresPerWindow}   ← 字段与冻结 schema 逐字一致
   窗口 120s（单调时钟）· 失败≥5 次关窗
   同屏显示手工码（32 字符 base32，与 QR 材料
   同源同熵）与材料校验码（SHA256 前 4 字符）
3. [无地址字段] 手机经 UDP 发现候选，按 QR 的       ──扫描/输入──▶
   computerId 匹配；发现不可用时手动输入地址
4. TLS 连 8766，按 certificateSha256 钉扎（首连即钉，无 TOFU）
5. POST /api/lan/pair/qr {pairingId, oneTimeMaterial,
   clientId, clientLabel}                ◀─校验：窗口未过期·材料未用·未达上限·
                                          pairingId 存在（未知 pairingId 按"窗口未开"
                                          404 语义处理，独立计数，不计入关窗上限，
                                          防 LAN 攻击者反复关窗 DoS）
6.【本机确认】托盘确认卡：clientLabel+clientId+作用域清单
   + 材料校验码（供与手机侧显示人工比对）
   30s 无操作或窗口到期未决 → 拒绝并收窗
7. 确认后签发：clientToken(≥192bit)、scopes、pairingId、
   issuedAt、certSha256；令牌仅出现在响应中
8. 材料立即作废（单次）· 窗口关闭 · 审计记录（仅 pairingId 与结果）
9. 手机存入 Keystore 封装存储（§7）             ◀──200 + 凭据──
```

- **手工兜底**：手工码与 QR 材料同源、同熵下限（32 字符 base32 = 160bit ≥ 契约 minLength=32/128bit，L-2 冻结答案）；单次、限速、短窗，不得当永久令牌，不得先忽略证书错误再发送密钥。
- **错误路径**：材料过期/已用/错误 → 统一 401（抗枚举；修订契约映射，L-1）；失败上限 → 429（登记 supplementary）；窗口未开/未知 pairingId → 404；确认超时/拒绝 → 403。
- **时钟**：窗口起止用接收端进程内单调时钟，墙上时间跳变不延长（V11）。
- **材料卫生**：不写日志、不进布局导出/诊断包/审计本体（NET-02/SEC-05）。
- **本机确认的防护边界（L-5e 写明）**：确认卡防"窗口内材料泄露后他人抢配"——攻击者需同时持有材料并抢在确认前提交，且确认卡的材料校验码提供人工比对防线；clientLabel 为请求方自报不可作为身份依据。
- **不变量（§10 同步）**：配对窗口开启与本机确认**只经托盘本地 UI，绝不暴露 8765/8766 HTTP 入口**。

## 4. 发现层（NET-01）

- mDNS `_phonedeck._tcp.local.`（TXT 与 UDP 应答同字段集，不含令牌/指纹）+ UDP 8767 回退；候选地址必须经 §3 钉扎+认证才触发输入/采音。**mDNS 留 A5（满足 NET-01 的 R1 起始门，不移 M2，L-6 处置）**；库候选（评审后定）：Makaretu.Dns / Zeroconf（均 MIT）。
- V13 回环面加固（Origin/Host 校验缺失为现状事实）**另立任务**，不混入本设计实现。

## 5. 旧 shared token 迁移（NET-07 / V17）

1. **共存窗口**：接收端同时接受旧 `X-PhoneDeck-Token`（隐式 `clientId=legacy-shared`，作用域=现状全集）与新凭据（`Authorization: Bearer` + `X-PhoneDeck-Client`）。旧头不获得任何新能力（不能撤销、不能 rotate）。
2. **升级路径（无需重新扫码）**：手机升级后首次用旧 token 连接即强提示升级；`POST /api/lan/credential/rotate`（旧 token 鉴权）→ 服务端生成**该手机专属** clientId+令牌并返回**一次**。**rotate 永不重发凭据（G-1 处置）**：同 clientId 再次 rotate 返回 `already-upgraded` 状态而非令牌本体；revoked clientId 永不重发；rotate 限速并逐条审计；rotate 签发的凭据在 clients.json 与托盘列表可见、可逐个撤销。提供"一键吊销旧入口签发的全部凭据"应急操作。
3. **legacy 撤销（M-4 处置）**：撤销列表显式列出"未升级旧凭据（legacy 入口）"条目；选择它即关闭整个旧入口，UI 明示"其余未升级手机需重新配对"；这是把粗粒度开关包装为显式告知的应急撤销。手机侧文案继续区分"忘记本机记录"与"服务端撤销"。
4. **窗口关闭**：默认"当前 + 下一稳定版"；设置页有"仅允许独立凭据"开关；关闭后旧 token → 401。
5. **回退**：手机保留旧 token 直至新凭据首次成功使用；rotate 生成但未使用的凭据可由用户删除重试。
6. **USB `/api/lan/pair` 端点归宿（L-4 处置）**：窗口期内继续签发 legacy 共享令牌（兼容老版本手机），响应附 `upgradeHint`；窗口关闭后该端点停止签发（返回 410 + 指引文案），不再制造 legacy 凭据。
7. **已知副作用（L-3 写明，V17 断言）**：窗口期内旧手机共享 `legacy-shared` 去重分区与限速桶，恶意旧手机可压制另一旧手机输入或触发全局限流；窗口有限，接受并在 V17 用例固化。

## 6. 撤销与会话终止（NET-03 / V12 / §16.3）

- 存储：`data/clients.json`（原子写+损坏备份，CFG-03 纪律）：`{clients:[{clientId, label, tokenHash, scopes, pairingId, issuedAt, revokedAt?}]}`——**只存哈希，无令牌本体，无 DPAPI 列**（G-2 处置）。
- **撤销顺序（M-2 处置）**：① 原子写 clients.json（revokedAt）+ 会话墓碑 → 写盘成功后 ② 删内存鉴权表（即时生效，目标 ≤1s，D03 冻结）③ 取消该 clientId 音频长流 CancellationToken ④ 取消宏剩余步骤并**反向释放本会话全部持有键（含引擎 hold 键，经会话停止路径）**（M-3 处置：撤销=停止优先；仅允许单个不可分割按键事件自然完成，释放预算并入 D03）⑤ 写审计。**写盘失败 → 中止撤销并告警，不产生"崩溃可复活"窗口**。
- 迟到重连/迟到 start 一律按墓碑拒绝（§16.3 规则 2/7）；另一手机零影响。
- 审计：签发/撤销/rotate 追加 `data/clients-audit.log`（有界轮转、脱敏、无材料/令牌本体）。
- **data 克隆立场（M-5 处置）**：DPAPI/令牌哈希设计使克隆机鉴权表不可用（净收益）；残留风险=克隆机持有同 computerId+同证书，手机端在"同 computerId 候选地址快速交替"时提示冲突并要求重新确认；记为已知残留风险，交 V14 验证。

## 7. 存储与保护（SEC-05）

| 端 | 方案 |
|---|---|
| Android | Android Keystore 生成 AES 密钥，凭据记录 GCM 封装后落 SharedPreferences（自写 ~60 行，无新依赖；`androidx.security:security-crypto` 为评审备选）——统一称"**Keystore 封装存储**"（L-5a 术语统一） |
| Windows | clients.json 仅含 tokenHash 等非机密字段，目录隔离沿用现有 data 目录纪律 |
| macOS | M2：Keychain |

凭据/材料不入 Git、普通导出（C6 夹具已有）、日志。

## 8. 与既有契约夹具的一致性

- QR 载荷字段名与 `qrPairingPayload`（additionalProperties=false）逐字对齐：`version/httpsPort/certificateSha256/pairingId/oneTimeMaterial/validSeconds/maxFailuresPerWindow`；**不含地址与 scopes**（地址来自发现层/手动输入；scopes 在确认卡与响应中，M-1 处置）。
- **夹具随实现同步（M-6 处置，不再捆绑 A5）**：A1 附 `clients.json` 存储契约与 429 补充状态；A2 附 QR 载荷修订（如需）与 `pair/qr` 请求/响应样本；A4 附 `credentialRotate` 样本与 `expiredMaterial→401` 错误映射修订（L-1）。每次过 `validate.py`。
- 协议版本：新端点/头以能力位通告（`qr-pairing`、`per-client-credentials`、`credential-rotation`），不改 v2 必填字段（PRO-01）。

## 9. 实现切分（评审通过，v1.0 起生效）

| 步 | 内容 | 端 | 验收 | 附带契约任务 |
|---|---|---|---|---|
| A1 | clientId/作用域/clients.json/鉴权中间件 + 撤销（含长流终止与键释放） | Windows | L1 单测 + V12 子集（L2 假流） | clients.json 契约 + 429 |
| A2 | QR 生成 + 配对窗口 + 本机确认 + 手工码 + USB pair 端点 upgradeHint | Windows | V09/V11（L2） | QR 载荷复核 + pair/qr 样本 |
| A3 | Android 扫码（zxing-android-embedded，Apache-2.0）+ 配对 UI + Keystore 封装存储 | Android | V09（L3 真机） | — |
| A4 | rotate（不重发语义）+ 手机迁移 + 共存窗口 + legacy 应急撤销 | 双端 | V17（L2+L3） | rotate 样本 + 错误映射修订 |
| A5 | mDNS + 发现候选匹配 | 双端 | V08 + V10（安全负例） | — |

依赖 A1→A2→A3→A4；A5 独立。D03 冻结项（≤1s、键释放预算、去重 TTL）在 A1 开工前确认。

## 10. 安全自检清单（实现与评审共用）

- [ ] 无自创密码协议：机密性=钉扎 TLS；授权=高熵材料/令牌比较（常量时间）。
- [ ] 发现应答不含令牌/指纹；未认证候选不触发输入/采音。
- [ ] 一次性材料：单次、限速、单调时钟短窗、不落日志/导出/审计本体。
- [ ] 服务端只存令牌哈希；rotate 永不重发；revoked 永不重发；手机端 Keystore 封装。
- [ ] 撤销：先持久化后生效；关闭长流；释放本会话持有键；墓碑拒迟到；跨手机零影响。
- [ ] 配对窗口与本机确认只经本地托盘 UI，无任何 HTTP 入口。
- [ ] 旧 token 降级不提权；legacy 应急撤销有显式告知；窗口关闭有版本边界。
- [ ] 无鉴权入口仍仅 127.0.0.1:8765；LAN 无明文降级。
- [ ] 签发/撤销/rotate 全审计（脱敏）。

## 11. 已决问题（原待评审项处置）

1. 凭据形态：**A（bearer）**，B 为升级候选。2. 手工码：**32 字符 base32（160bit），与 QR 材料同源同熵**。3. Android 存储：**自写 Keystore 封装**。4. 扫码库：**zxing-android-embedded（Apache-2.0）**。5. mDNS：**留 A5**。6. 确认超时：**30s，窗口到期未决=拒绝收窗**；每次提交必经本机确认。7. 撤销对进行中输入：**停止优先+释放持有键**（§6）。
遗留开放项：USB pair 端点的 Origin/Host 加固（V13 面）另立任务跟踪；QR 512B 上限 A2 实测复核。

## 12. 评审记录（2026-09-29 独立评审，全新上下文只读）

**结论：修改后可进入实现。** 17 项意见（高 2 / 中 6 / 低 6 + 净通过确认）全部处置：

| 编号 | 摘要 | 处置 |
|---|---|---|
| G-1/G-2 | rotate 幂等可冒领他人凭据（撤销旁路）；"只存哈希/DPAPI/重发"三者互斥 | §5.2/§6：服务端只存哈希；rotate 永不重发（already-upgraded）；revoked 永不重发；找回=重新扫码；应急一键吊销 |
| M-1 | QR 载荷与冻结 schema 冲突、缺 pairingId、ipList/scopes 越界 | §3/§8：字段逐字对齐 schema；地址不进 QR；scopes 移确认卡/响应；夹具任务随步 |
| M-2 | 撤销持久化顺序未定义，崩溃可复活 | §6：先原子写 revokedAt/墓碑再删内存表，写盘失败中止 |
| M-3 | 撤销后宏/引擎键未释放 | §6：停止优先+反向释放持有键，预算入 D03 |
| M-4 | legacy 手机无撤销对象 | §5.3：legacy 应急撤销条目+显式告知+首连强提示 |
| M-5 | data 克隆无立场 | §2/§6：DPAPI 净收益写明+地址交替冲突提示+残留风险交 V14 |
| M-6 | 验收漏 V10/V13；夹具捆绑 A5 | §9：V10 挂 A5；V13 另立任务；夹具随 A1/A2/A4 |
| L-1…L-6 | 错误码 401 统一、手工码 32 字符、legacy 分区副作用、USB pair 归宿、术语/时序/未知 pairingId/确认边界、mDNS 留 A5 | §3/§5/§7/§9/§11 全部落档 |

净通过确认：无自创密码协议嫌疑；NET-01/02/04/05/06 满足；§16.3 规则 2/7 一致；QR 含材料的泄漏面可接受（短窗+单次+确认+校验码）。
候选参数冻结：validSeconds=120、maxFailuresPerWindow=5、oneTimeMaterial ≥32 字符/≥128bit、手工码 32 字符 base32、撤销生效 ≤1s（并入 D03）——同步回 `contracts/pairing-and-identity.json`（实现任务 A1/A2 执行）。
