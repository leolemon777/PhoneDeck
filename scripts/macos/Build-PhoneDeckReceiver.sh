#!/bin/zsh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$ROOT_DIR/work/phone-deck/macos/PhoneDeck.Receiver/PhoneDeck.Receiver.csproj"
PLIST="$ROOT_DIR/work/phone-deck/macos/PhoneDeck.Receiver/Packaging/Info.plist"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"

REQUESTED_ARCH="${1:-$(uname -m)}"
case "$REQUESTED_ARCH" in
  arm64|apple-silicon)
    RID="osx-arm64"
    ;;
  x86_64|x64|intel)
    RID="osx-x64"
    ;;
  *)
    echo "不支持的架构：$REQUESTED_ARCH（可用 arm64 或 x64）" >&2
    exit 2
    ;;
esac

DIST_ROOT="$ROOT_DIR/work/phone-deck/dist/macos/$RID"
PUBLISH_DIR="$DIST_ROOT/publish"
APP_DIR="$DIST_ROOT/PhoneDeck Receiver.app"

case "$APP_DIR" in
  "$ROOT_DIR"/work/phone-deck/dist/macos/*/PhoneDeck\ Receiver.app) ;;
  *)
    echo "拒绝清理非预期输出目录：$APP_DIR" >&2
    exit 3
    ;;
esac

rm -rf -- "$PUBLISH_DIR" "$APP_DIR"
mkdir -p "$PUBLISH_DIR" "$APP_DIR/Contents/MacOS"

"$DOTNET_BIN" publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -o "$PUBLISH_DIR"

cp "$PLIST" "$APP_DIR/Contents/Info.plist"
cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"
chmod 755 "$APP_DIR/Contents/MacOS/PhoneDeck.Receiver"

# 本地开发包使用稳定 Bundle ID 的 ad-hoc 签名，便于 macOS 记录辅助功能授权。
/usr/bin/codesign --force --deep --sign - \
  --identifier com.codex.phonedeck.receiver \
  "$APP_DIR"

echo "已生成：$APP_DIR"
echo "把 App 移到 /Applications 后再授予辅助功能权限，避免路径变化导致授权失效。"
