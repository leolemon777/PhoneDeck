# Android 固定签名迁移准备

用户于 2026-10-29 授权先准备迁移并验证恢复，完成后再确认手机安装。本阶段不授权卸载现装应用。
此维护工具不进入生产 APK，也不增加远程命令入口；所有设备都必须由显式 ADB 序列号指定。

## 当前材料与约束

- 原包 `com.codex.phonedeck`，dev.22/code28，证书前缀 `653884d0`；现有固定签名候选证书前缀 `df327953`。
- 原签名私钥尚未找回；安装包只有公开证书，不能据此重新签名。不同签名不能直接覆盖当前安装。
- 私有文件归档包含快捷键、设置和旧配对密文。旧 AndroidKeyStore 密钥不会随这些文件导出，归档本身不保证卸载后恢复配对。
- 已通过旧应用 UID 读取并解密 3 台电脑记录，在当前 Windows 用户下生成 DPAPI 加密备份并回读校验。
  不导出 AndroidKeyStore 密钥，不把配对明文写到文件或终端，也不把个人备份提交 Git。
- 原机快捷键为 37 项（6 自定义、22 隐藏），最终应用迁移函数离线执行未改变动作、顺序或显示配置。
- 当前候选是已构建并通过 66 项测试的固定签名 debug 包；不是公开发布或正式 release 验收。

## 工具

- `scripts/android/migration/PairingMigration.java`：独立 `app_process` helper，导出、严格写入加密记录、逐字段核对。
- `scripts/android/migration/Invoke-PairingMigration.ps1`：构建 DEX helper、校验传输摘要、捕获敏感输出、DPAPI 备份及回读。
  `Import` 必须指定 `-TestEmulator`，且只接受真正的 Android 模拟器；当前工具不允许导入物理手机。
- `scripts/android/migration/Restore-EmulatorSettings.ps1`：只向模拟器恢复三个白名单文件并核对 SHA-256，拒绝替换不同内容。

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

Helper 代码可以经 `/data/local/tmp` 暂存，但凭据载荷只走受控进程内存和 ADB stdin。helper 本身先校验 SHA-256，再在应用 UID 内运行。
`app_process` 的独立运行环境需注册 Android 的 Keystore provider；不更改系统安全策略、SELinux、root 权限或签名验证。
导入/核对使用 Base64 文本与 `adb shell -T -e none`，不能用不转发 stdin 的 `adb exec-out`。

导入必须先确认目标配对 XML/备份文件不存在。新记录使用目标应用自己的 AES-GCM 密钥重新加密，临时文件先解密核对全部字段，
再用 `CREATE_NEW` 独占创建目标，写入/刷新后再次解密核对。不会覆盖既有文件，但不声称发布过程具有完整原子性。
加密失败不回退明文。若失败时刚生成了密钥，保留该密钥；目标可能有部分内容，必须停止并检查恢复，不能自动清除或启动应用。
这不等于卸载回滚，恢复依赖已验证的 DPAPI 备份。

DPAPI 文件只允许当前 Windows 用户读取，恢复也需要同一用户可用的 DPAPI 环境。文件不可解开、校验失败或缺少记录时必须停止，不能先卸载再补救。
强制终止脚本或拔线可能留下临时 helper；重试前检查并仅清理工具命名的临时文件，不能删除用户配置目录。

## 验收与安装确认

2026-10-29 已在电脑上的隔离 API 31 模拟器执行固定签名候选恢复：3 台配对及全部字段在导入后、候选冷启动后均核对一致；
快捷键、`rList` 和设置三份文件在启动后仍与原手机快照逐字节一致。首页指令库和共享控件可见，未自动启动麦克风服务。
本机 DPAPI/ACL、备份拒绝覆盖、损坏密文拒绝和物理 Import 门禁合成回归通过。原机只读 Verify 也通过，用户文件与快照一致，原应用已重新打开。

准备阶段需要在隔离、断网的 Android 测试环境中完成：

1. 固定签名候选全新安装，获得与原机不同的 UID/KeyStore。
2. 真实加密备份导入后，原 3 台电脑的 ID、凭据、证书指纹、地址、默认目标和共享组逐字段相同。
3. 新 XML 只有 `known_devices_enc`，没有明文 `known_devices`。
4. 快捷键和设置文件恢复后摘要相同；候选启动、停止后再次核对。
5. 拒绝重复导入，拒绝损坏备份，并确认原有测试数据不被覆盖。
6. 原手机的用户文件仍与快照一致，临时 helper 清理完成。

模拟器演练通过仍不代表 Samsung 的新安装、无线连接或共享音频已验收。用户确认后才进入手机阶段：
更新备份 → 停止应用 → 卸载旧签名包 → 安装指定固定签名候选 → 在新 UID 内恢复并重新加密 → 核对设置/配对 → 必要的系统权限确认 → 实机连接与音频测试。
在用户确认之前保持手机原安装。执行手机阶段前需单独评审工具对物理设备的启用，不能临时删掉模拟器保护直接运行。

已安装的同一旧 APK 也不能在卸载后自动带回旧 Keystore；若新版本功能回退，应优先用同一固定签名构建旧源码，再覆盖安装。
若必须回到旧签名，仍需使用已经验证可解开的迁移备份重新加密，不能只还原旧密文。

机制依据：[Android 应用签名](https://developer.android.com/studio/publish/app-signing)、
[Android Keystore](https://developer.android.com/privacy-and-security/keystore)、
[Android 调试](https://developer.android.com/studio/debug)。最新执行结果以 `docs/HANDOFF.md` 为准。
