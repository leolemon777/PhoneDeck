# 文档目录

根目录只放每次开工都要读的三份文档，其余按用途分到子目录。

| 位置 | 内容 |
| --- | --- |
| [HANDOFF.md](HANDOFF.md) | 最新状态、待办与验证记录（每次完成工作后更新） |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 三端结构、协议与端口 |
| [BUILD_PIPELINE.md](BUILD_PIPELINE.md) | 构建、打包、版本与发布流程 |
| [guides/](guides) | 安装、部署和使用：开发环境、Mac / Windows 接收端、手机网页、语音引擎、设备统一更新 |
| [design/](design) | 设计方案：配对与授权安全设计、界面翻新、品牌 |
| [release/](release) | 开源发布规格、发布门清单、支持矩阵、已知问题、第三方声明、跨平台执行与验收记录 |
| [archive/](archive) | 已被取代的历史记录，仅供追溯，不作为执行依据 |
| [images/](images) | README 使用的截图 |

产品范围和决策仍以仓库根目录的 `spec plan.markdown` 为准。

## guides

- [SETUP.md](guides/SETUP.md)：新电脑开发、构建与运行
- [MACOS_SETUP.md](guides/MACOS_SETUP.md)：macOS 接收端
- [WINDOWS_WIFI_DEPLOY.md](guides/WINDOWS_WIFI_DEPLOY.md)：Windows 电脑部署（同一 Wi-Fi 点按连接）
- [DESKTOP_QUICK_START.md](guides/DESKTOP_QUICK_START.md)：2.0 跨平台预览首次使用（打包时复制为 START-HERE.md）
- [IPHONE_PWA.md](guides/IPHONE_PWA.md)、[TYPELESS_PWA.md](guides/TYPELESS_PWA.md)：手机网页路线
- [VOICE_ENGINES.md](guides/VOICE_ENGINES.md)：语音引擎档案
- [PHONE_MANAGED_DESKTOP.md](guides/PHONE_MANAGED_DESKTOP.md)：手机统一设置
- [FLEET_UPDATES.md](guides/FLEET_UPDATES.md)：设备统一更新

## archive

B02 / B03 实施约定、旧的 Mac 与项目交接、Windows 本机交付与内存优化记录、Console v2 重设计方案，以及已被「附近 · 点按连接」取代的扫码配对指南。
