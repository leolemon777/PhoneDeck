# Implementation Notes

## Spec Interpretation
- Deliver a high-fidelity, interactive Windows desktop console prototype for PhoneDeck 1.6.0-dev.4, not a marketing page.
- Reuse real Control Center fields and behaviors from `PhoneDeck.ControlCenter` / health API: receiver, Wi-Fi, phone audio, USB fallback, start/restart/stop, LAN discovery, USB watchdog, autostart, ADB path, Agent shortcuts, local logs.
- Visual direction is locked by the brief: macOS utility feel, soft gray scale, deep ink typography, restrained blue + green status, 14px radius, no gradients or neon effects.

## Decisions Made
- Single 1200×820 window with Win11 caption chrome; scene chips sit outside the window so empty / offline / USB states can be reviewed without polluting the product UI.
- Default operational view is Wi-Fi ready (the real happy path). Empty, offline, and USB connected are first-class visual states.
- Nav has five real surfaces: 概览, 连接, 设备, 日志, 设置. Overview matches the requested topology + collapsible settings + log stream.
- Tokens: IBM Plex Sans / Noto Sans SC / IBM Plex Mono; macOS-like #f5f5f7 canvas, white surfaces, hairline gray borders, compact controls; 14px radius; no gradient fills.

## Changes From Spec
- Added a fourth preview scene (无线就绪) so start/stop have a healthy target. The three requested states remain explicit.
- Log “清空显示” is local to the prototype viewer; the WinForms app has no clear action.

## Tradeoffs
- HTML/CSS/JS prototype rather than rewriting WinForms in this OpenDesign pass. Field names, copy, and state transitions follow the existing C# console.
- Topology is schematic, not a live packet graph.

## Verification
- Browser pass against empty / offline / USB / Wi-Fi scenes, nav pages, start-restart-stop, settings save, Agent modal validation, log filters, collapsed settings.

## Risks / Follow-up
- Prototype does not spawn `PhoneDeck.Server.exe`. Wiring this UI back into `ControlCenterForm.cs` is a separate implementation task.
