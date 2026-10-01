# 言渡 · Yandu

2026-10-01：用户确认名称「言渡 · Yandu」，随后指定 APK 图标采用 Notion 风格的黑白话筒。本轮落实名称与图标；墨水屏首页、圆形语音按钮仍是设计效果图。

## 显示名称与升级

Android launcher、首页、设置说明、共享麦克风通知及新 Desktop 控制页使用「言渡」。Desktop 的副标题为「Yandu · 多设备语音输入」，桌面安装菜单显示新名称。

Android 的固定签名分发渠道仍为 `com.codex.phonedeck.desktoppreview`，versionName 为 `2.0.0-alpha.1`，versionCode 从 1 增至 2；新包可覆盖同签名的上一预览包。「言渡 UI 预览」是独立开发渠道。

PhoneDeck 的包名、协议名、偏好键、证书、数据目录、程序集和执行文件名作为兼容标识继续使用，避免改名产生新设备身份或丢失配对。GitHub 仓库当前仍为 PhoneDeck。旧接收端和旧固定三产物更新的版本没有更改。

## APK 图标

原创矢量母版为 `design/yandu-icon.svg`：暖白 `#F5F3EE`、墨黑 `#20201E`，细线框中的话筒。图标没有字母、品牌文字或第三方商标；Notion 只作为用户指定的视觉风格参考。

- `drawable/ic_launcher_foreground.xml` 是透明的黑色前景，背景独立。
- 默认和夜间主题共用同一图标；Android 8+ 支持自适应裁切，API 33+ 提供单色主题图层。
- 普通与圆形 launcher 均引用 `@mipmap/ic_launcher`。
- 五档 PNG 和 1024 px 母版由 SVG 确定性生成；素材无外部图片和字体依赖。
- 共享麦克风通知使用系统的话筒小图标及新名称，不将完整 launcher 底板作为通知图标。

可选生成命令（Windows WPF，仅贡献者重绘资产时需要）：

```powershell
pwsh -NoProfile -File scripts/branding/Build-YanduIcons.ps1
```

Android 在 Windows/Mac/Linux 直接使用已提交的 XML/PNG，不需要运行渲染脚本。SVG 也用于新 Desktop 控制页的浏览器图标。平台图层规则见 [Android 自适应图标文档](https://developer.android.com/develop/ui/views/launch/icon_design_adaptive)。
