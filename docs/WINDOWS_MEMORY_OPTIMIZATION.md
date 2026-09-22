# Windows 控制台内存优化（2026-09-21）

> 历史 WPF 优化记录，对应提交 `0479e1c`。当前原生托盘实现和测量见 [手机统一设置](PHONE_MANAGED_DESKTOP.md)。
> 下方 WPF 探针须在该历史提交运行；当前同名脚本已改为只读采样真实安装进程。

范围：现有 Windows 1.6 开发线的控制台实现优化；不更改接收端、Android、协议、音频格式、签名渠道或发布序号。
基线提交 `9efb46c`。这是本机开发验证，不是已经发布的新版本。

## 改动与原因

- 卡片和导航容器使用已有细边框区分层级，移除大面积模糊阴影的离屏渲染缓存。
  按钮的小阴影、布局、文字和操作保留。
- 主窗口和 Agent 编辑器采用不透明窗口，由 Windows 11 DWM 处理外轮廓圆角；
  旧 Windows 回退方角。取消整窗透明缓冲与每次尺寸变化的裁剪几何重建。
- 主题资源依赖链变为 Generic → Styles → Fonts → Colors，Icons 仅由 Styles 加载；
  不重复构造颜色、字体和图形字典。
- 托盘隐藏和最小化时停止界面定时器；恢复后如果快照超过原来的 2.5 秒刷新周期，立即刷新。
  接收端的诊断、USB 恢复、手机连接和音频服务继续独立运行，频率不变。
- 网卡枚举移到线程池，避免恢复窗口时阻塞 UI；热键每次读取最新的共享请求状态，
  不依赖隐藏前的缓存，并合并尚未完成时的重复热键。读取失败不发送猜测的切换请求。
- 只读日志关闭撤销历史，继续限制每份文档 180 条；图标按显示尺寸解码、主题不变时复用，
  资源流、进程句柄及托盘资源及时释放。
- 移除 `EmptyWorkingSet`。它降低驻留计数但没有释放私有分配，且会让恢复窗口重新调页。
  不增加强制 GC、定时清内存、音频降采样、较慢连接轮询或有损压缩。

Windows 官方机制参考：[DWM 圆角](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)、
[WPF 透明窗口](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency)、
[WPF 资源共享](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-application-resources)。

## 同机对照

环境：Windows build 26200、64 GiB RAM；固定 SDK 8.0.425 / 运行时 8.0.31，Release、自包含 win-x64。
探针使用真实 WPF 控件树、相同健康/引擎只读接口、相同桌面和窗口大小，先显示 5 秒，
隐藏约 12 秒，再执行 10 次恢复。测量之前不手动 GC。指标单位 MiB（表中简称 MB）。

| 指标 | 修改前（两次基线） | 最终实现 |
|---|---:|---:|
| 显示时私有提交内存 | 211.7 / 232.9 MB | 113.0 MB |
| 隐藏后私有提交内存 | 210.1 / 231.4 MB | 108.8 MB |
| 反复恢复后私有提交内存 | 252.5 / 234.0 MB | 98.6 MB |
| 显示时工作集（含共享页） | 171.0 / 178.4 MB | 150.4 MB |
| WPF 首帧耗时 | 730 / 736 ms | 584 ms |
| 10 次恢复至 dispatcher idle 的均值 | 5.5 / 18.0 ms | 8.1 ms |
| 隐藏稳定 11 秒期间 CPU 时间增量 | 125 / 94 ms | 0 ms（计时器精度内） |
| 同期间托管分配增量 | 335,384 / 77,296 bytes | 3,312 bytes |
| 隐藏 / 最小化定时器 | 仍开启 | 停止 |

私有提交内存下降约一半，是减少资源分配的结果；不能把它写成所有机器的任务管理器读数减半。
工作集、私有工作集和私有提交是不同口径：旧代码隐藏时主动挤出工作集，新代码取消这一操作，
所以隐藏后的工作集不能单独作收益指标。本机原安装进程的长时状态也不能替代相同启动条件的对照。
首帧不含系统加载进程前的启动时间；恢复计时不代表网络状态刷新已全部完成。
本轮没有缩减 .NET 自包含运行时，EXE 仍约 154.4 MB，不把内存优化说成安装体积减少。

原始结果保存在忽略目录：

- `outputs/memory-review/baseline-run1/metrics.json`
- `outputs/memory-review/baseline-src/outputs/control-center-probe/dcea5e4fe7b1488391f64121568f44f8/metrics.json`
- `outputs/control-center-probe/9ef2f4abc8944bc4b849ec19dcc864d0/metrics.json`
- 相同目录的 `overview.png` / `settings.png` 已目视检查，界面完整。

## 验证与复现

```powershell
# 使用仓库 global.json 指定的 SDK，需真实 Windows 桌面会话。
pwsh -File scripts/performance/Measure-ControlCenter.ps1 -Verify
# -SourceRoot 可指定旧版本 worktree；旧版比较不传 -Verify。
# 非 PATH 工具链可传 -DotNetPath <dotnet.exe 的绝对路径>。
```

脚本输出独立 `outputs/control-center-probe/<id>`。探针通过注入临时入口跳过正常启动时的
接收端进程管理；不启动/停止接收端、不保存设置、不发送真实按键、不控制真实录音。
热键测试在窗口隐藏后替换为内存 HTTP handler，验证过期缓存、连续切换、断网和并发按键。
此外验证隐藏/恢复/最小化定时器、五个页面、Agent 编辑器打开，以及 500 条日志后两份历史均为 180 条且无撤销数据。
测试入口仅进入隔离构建目录，`instrumented-only` 内的 EXE **不能安装**；普通构建不包含探针。

本轮通过：

- 最终控制台 Release 自包含单文件发布，0 编译错误；确认正常入口为 `PhoneDeck.ControlCenter.App`。
- 接收端 Release 构建 0 警告 / 0 错误，现有 86/86 测试通过。
- 上述桌面行为断言全部通过；反复恢复未出现界面卡住。

待办：不同 DPI / 多屏 / 旧 Windows 的边框与最大化实机回归、连续数小时驻留，
以及实际手机开始/停止/听写回归。窗口保留控件树以保证快速恢复；本轮没有将 WPF 控制台重构为独立托盘宿主。
接收端音频实现未改，不据此声称所有硬件场景已经证明零性能回退。

普通优化版产物：`outputs/memory-review/final/console/PhoneDeck.ControlCenter.exe`。
当前使用中的安装目录和接收端未替换；正式分发仍需版本递增和既有发行流程。
