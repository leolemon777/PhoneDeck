# Android 固定签名迁移与物理恢复指南

2026-10-29 已完成用户批准的 Samsung 实体迁移：卸载旧签名包，安装固定签名候选，并在新 UID 内用已验证备份
重新加密恢复。此维护工具不进入生产 APK，也不增加远程命令入口；所有设备都必须由显式 ADB 序列号指定。

## 当前实体机结果

- 设备为 Samsung `SM-G9880` / Android 12（API 31），当前 `com.codex.phonedeck` UID 为 `10852`。
- 安装候选为 `outputs/ui-stability-device-20261029/candidate/Yandu-ui-stability-fixed-signature-candidate.apk`，
  SHA-256 为 `6053c39aaa64be734052c7e3def7e51e875aabfcc4f2c12a8a817e5b0097b763`。
- 导入后完整 Verify 通过；冷启动后稳定字段无变化，仅一条记录的 `lastSeenAt` 正常变化。3 台配对、共享组中的 3 台电脑、
  默认电脑和 37 个快捷键（6 个自定义、22 个隐藏）保留。只读证据在
  `outputs/ui-stability-device-20261029/phone-install-20261029/`。
- 手机与 PC 恢复到同一 Wi-Fi（PC `192.168.0.193`、手机 `192.168.0.195`）后物理拔 USB，ADB 已无设备，
  TCP 8766 已建立；`wifi-unplugged-levels.txt` 共 35 条逐秒记录、0 条零信号，最终时长 35.1 秒且每条有非零
  `CABLE Output` 电平。
- 30 秒供音测量窗口中途执行 `KEYCODE_HOME` 回到手机桌面后仍持续有信号；当时 USB 与 Wi-Fi 同时保留，不能把它当作
  Wi-Fi-only 锁屏/Doze 长时验收。
  Windows 仍是旧 dev.17 接收端，Typeless 仍为 AutoDetect 并选中物理麦克风；当前不宣称听写端到端通过。

## 历史准备阶段

用户最初选择“先准备迁移，验证后再确认手机安装”，因此准备阶段只在隔离模拟器中恢复，没有卸载实体机旧包。
该阶段已经结束；实体安装和实测以本指南“当前实体机结果”为准。以下材料、模拟器证据和限制仍作为审计记录保留。

## 当前材料与约束

- 原包 `com.codex.phonedeck`，dev.22/code28，证书前缀 `653884d0`；现有固定签名候选证书前缀 `df327953`。
- 原签名私钥尚未找回；安装包只有公开证书，不能据此重新签名。不同签名不能直接覆盖当前安装。
- 私有文件归档包含快捷键、设置和旧配对密文。旧 AndroidKeyStore 密钥不会随这些文件导出，归档本身不保证卸载后恢复配对。
- 已通过旧应用 UID 读取并解密 3 台电脑记录，在当前 Windows 用户下生成 DPAPI 加密备份并回读校验。
  不导出 AndroidKeyStore 密钥，不把配对明文写到文件或终端，也不把个人备份提交 Git。
- 原机快捷键为 37 项（6 自定义、22 隐藏），最终应用迁移函数离线执行未改变动作、顺序或显示配置。
- 当前候选是已构建并通过 66 项测试的固定签名 debug 包；实体安装已完成，但它不是公开发布或正式 release 验收。

## 工具

- `scripts/android/migration/PairingMigration.java`：独立 `app_process` helper，导出、严格写入加密记录、逐字段核对。
- `scripts/android/migration/Invoke-PairingMigration.ps1`：构建 DEX helper、校验传输摘要、捕获敏感输出、DPAPI 备份及回读。
  `Import -TestEmulator` 只接受真正的 Android 模拟器；物理 `Import` 必须额外提供批准 serial 和已安装候选 APK hash，
  并执行停止状态、唯一 APK 路径、拉取后的本机 SHA-256 校验。
- `scripts/android/migration/Restore-EmulatorSettings.ps1`：默认只向模拟器恢复三个白名单文件并核对 SHA-256；物理路径同样要求
  `-ApprovedPhysicalSerial` 与 `-ExpectedApkSha256`，先核对已安装候选 APK，再写入设置。

示例均需使用明确路径，不能自动选择第一台 ADB 设备：

```powershell
$parameters = @{
    Adb = '<adb.exe absolute path>'
    Serial = '<explicit serial>'
    JavaHome = '<JDK 17 directory>'
    AndroidSdk = '<Android SDK directory>'
    BackupFile = '<new local ignored DPAPI backup path>'
}
# 应用与共享麦克风必须已经停止。仅生成新备份，不覆盖旧备份。
./scripts/android/migration/Invoke-PairingMigration.ps1 -Mode Export @parameters

# 只在电脑上的隔离模拟器演练；不向手机执行。
$parameters.Serial = 'emulator-<port>'
./scripts/android/migration/Invoke-PairingMigration.ps1 -Mode Import -TestEmulator @parameters
./scripts/android/migration/Invoke-PairingMigration.ps1 -Mode Verify @parameters
```

物理设备只允许在用户已核对目标 serial、候选 APK 文件和摘要后执行；两个参数必须与目标完全对应，不能省略或写成另一台设备：

```powershell
$physicalParameters = @{
    Adb = '<adb.exe absolute path>'
    Serial = '<approved physical serial>'
    ApprovedPhysicalSerial = '<the same approved physical serial>'
    ExpectedApkSha256 = '6053c39aaa64be734052c7e3def7e51e875aabfcc4f2c12a8a817e5b0097b763'
    JavaHome = '<JDK 17 directory>'
    AndroidSdk = '<Android SDK directory>'
    BackupFile = '<verified local DPAPI backup path>'
}
./scripts/android/migration/Invoke-PairingMigration.ps1 -Mode Import @physicalParameters
```

缺少批准 serial、serial 不完全匹配、目标是 emulator、hash 缺失/格式错误、应用未停止、`pm path` 不是唯一允许路径、
拉取 APK 后的本机摘要不匹配或设备状态不确定时，脚本必须在 helper staging/import 前拒绝；不会自动选择其他 ADB 设备。

Helper 代码可以经 `/data/local/tmp` 暂存，但凭据载荷只走受控进程内存和 ADB stdin。helper 本身先校验 SHA-256，再在应用 UID 内运行。
`app_process` 的独立运行环境需注册 Android 的 Keystore provider；不更改系统安全策略、SELinux、root 权限或签名验证。
导入/核对使用 Base64 文本与 `adb shell -T -e none`，不能用不转发 stdin 的 `adb exec-out`。
物理 Import 在读取 APK 前确认 PhoneDeck 已停止；从 `pm path` 得到的唯一 APK 被拉到工具生成的临时目录，
由本机 `Get-FileHash -Algorithm SHA256` 与批准摘要比较。临时目录只允许清理工具自己生成的路径，校验或清理失败均报告错误。

导入必须先确认目标配对 XML/备份文件不存在。新记录使用目标应用自己的 AES-GCM 密钥重新加密，临时文件先解密核对全部字段，
再用 `CREATE_NEW` 独占创建目标，写入/刷新后再次解密核对。不会覆盖既有文件，但不声称发布过程具有完整原子性。
加密失败不回退明文。若失败时刚生成了密钥，保留该密钥；目标可能有部分内容，必须停止并检查恢复，不能自动清除或启动应用。
这不等于卸载回滚，恢复依赖已验证的 DPAPI 备份；如果要回退固定签名候选，仍按同一物理 serial/hash 门禁执行。

DPAPI 文件只允许当前 Windows 用户读取，恢复也需要同一用户可用的 DPAPI 环境。文件不可解开、校验失败或缺少记录时必须停止，不能先卸载再补救。
强制终止脚本或拔线可能留下临时 helper；重试前检查并仅清理工具命名的临时文件，不能删除用户配置目录。

## 验收记录与剩余范围

### 已完成

2026-10-29 已在隔离 API 31 模拟器完成固定签名候选恢复：3 台配对及全部字段在导入、冷启动后均核对一致；
快捷键、`rList` 和设置三份文件逐字节一致，首页指令库和共享控件可见，未自动启动麦克风服务。本机 DPAPI/ACL、
备份拒绝覆盖、损坏密文拒绝、物理 Import 门禁和脚本语法回归通过。

随后在 Samsung 实体机完成用户批准的卸载、固定签名安装和新 UID 恢复；完整 Verify、冷启动稳定字段、3 台配对、
共享组中的 3 台电脑、默认电脑、快捷键和无 USB Wi-Fi 音频输出证据见上方“当前实体机结果”。

### 当前仍待测

- 部署连接稳定性分支的 Windows 候选后，再做无 USB Wi-Fi 的连接、共享和停止/重连回归；当前实测使用旧 dev.17 接收端。
- 在 Typeless 中明确选择 `CABLE Output`，单独验证手机音频进入真实听写并产生文字；不能由 CABLE 电平或接收端 streaming 状态推断。
- 执行 1/2/3 台电脑共享、锁屏/Doze、切网、拔线、接收端重启、长时运行和多电脑部分停止矩阵。
- 保留并核对恢复后的备份、临时 helper 清理及失败回退证据；不以模拟器或单次电平结果替代硬件验收。

### 历史准备阶段验收项（已完成并保留）

1. 固定签名候选在隔离环境获得与原机不同的 UID/KeyStore。
2. 加密备份导入后，原 3 台电脑的 ID、凭据、证书指纹、地址、默认目标和共享组逐字段相同。
3. 新 XML 只有 `known_devices_enc`，没有明文 `known_devices`。
4. 快捷键和设置文件恢复后摘要相同；候选启动、停止后再次核对。
5. 重复导入和损坏备份被拒绝，原有测试数据未被覆盖。
6. 原手机用户文件与快照一致，临时 helper 清理完成。

已安装的同一旧 APK 也不能在卸载后自动带回旧 Keystore；若新版本功能回退，应优先用同一固定签名构建旧源码，再覆盖安装。
若必须回到旧签名，仍需使用已经验证可解开的迁移备份重新加密，不能只还原旧密文。

机制依据：[Android 应用签名](https://developer.android.com/studio/publish/app-signing)、
[Android Keystore](https://developer.android.com/privacy-and-security/keystore)、
[Android 调试](https://developer.android.com/studio/debug)。最新执行结果以 `docs/HANDOFF.md` 为准。
