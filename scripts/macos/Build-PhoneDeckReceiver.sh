#!/bin/zsh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$ROOT_DIR/work/phone-deck/macos/PhoneDeck.Receiver/PhoneDeck.Receiver.csproj"
PLIST="$ROOT_DIR/work/phone-deck/macos/PhoneDeck.Receiver/Packaging/Info.plist"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"

REQUESTED_ARCH="${1:-$(uname -m)}"
# 第二个参数：lite（默认，原生编译的精简信号接收端，约 20 MB 内存）或 full（含 iPhone 浏览器网关的 JIT 版）。
VARIANT="${2:-lite}"
case "$VARIANT" in
  lite|full) ;;
  *)
    echo "不支持的版本：$VARIANT（可用 lite 或 full）" >&2
    exit 2
    ;;
esac
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

if [[ "$VARIANT" == "lite" ]]; then
  # Native AOT：需要 Xcode 命令行工具（clang）。不含 iPhone 浏览器网关与二维码图片。
  "$DOTNET_BIN" publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    -p:PublishAot=true \
    -o "$PUBLISH_DIR"
else
  "$DOTNET_BIN" publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -o "$PUBLISH_DIR"
fi

cp "$PLIST" "$APP_DIR/Contents/Info.plist"
# 只复制可执行文件与运行所需资源，不带 .dbg/.pdb 等调试文件。
find "$PUBLISH_DIR" -maxdepth 1 -type f ! -name "*.dbg" ! -name "*.pdb" ! -name "*.dSYM" \
  -exec cp {} "$APP_DIR/Contents/MacOS/" \;
chmod 755 "$APP_DIR/Contents/MacOS/PhoneDeck.Receiver"

# 本地开发包使用稳定 Bundle ID 的 ad-hoc 签名，便于 macOS 记录辅助功能授权。
/usr/bin/codesign --force --deep --sign - \
  --identifier com.codex.phonedeck.receiver \
  "$APP_DIR"

echo "已生成（$VARIANT）：$APP_DIR"
echo "把 App 移到 /Applications 后再授予辅助功能权限，避免路径变化导致授权失效。"
