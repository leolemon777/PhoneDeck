#!/usr/bin/env bash
# PhoneDeck 统一开发构建入口 (B01)
# 支持 Linux / macOS / Windows(Bash) 统一构建、测试与报告归档

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

PLATFORM="All"
CONFIGURATION="Release"
OUTPUT_DIR="outputs/build-review"
CLEAN=false
SKIP_TESTS=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    -p|--platform)
      PLATFORM="$2"
      shift 2
      ;;
    -c|--configuration)
      CONFIGURATION="$2"
      shift 2
      ;;
    -o|--output-dir)
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --clean)
      CLEAN=true
      shift
      ;;
    --skip-tests)
      SKIP_TESTS=true
      shift
      ;;
    -h|--help)
      echo "用法: $0 [-p All|Windows|Android|MacOS] [-c Release|Debug] [-o 输出目录] [--clean] [--skip-tests]"
      exit 0
      ;;
    *)
      echo "未知参数: $1" >&2
      exit 1
      ;;
  esac
done

if [[ "$OUTPUT_DIR" = /* ]]; then
  OUT_FULL="$OUTPUT_DIR"
else
  OUT_FULL="$REPO_ROOT/$OUTPUT_DIR"
fi

REPORTS_DIR="$OUT_FULL/reports"

echo "=================================================="
echo "PhoneDeck 统一开发构建入口 (B01 - Bash)"
echo "=================================================="
echo "仓库根目录: $REPO_ROOT"
echo "目标平台:   $PLATFORM"
echo "构建配置:   $CONFIGURATION"
echo "输出目录:   $OUT_FULL"
echo "=================================================="

if [ "$CLEAN" = true ]; then
  echo -e "\n>>> [1/4] 清理历史产物..."
  rm -rf "$OUT_FULL"
fi

mkdir -p "$OUT_FULL" "$REPORTS_DIR"

# 1. Windows 构建
if [ "$PLATFORM" = "All" ] || [ "$PLATFORM" = "Windows" ]; then
  echo -e "\n>>> 执行 Windows 平台构建与验证..."
  WIN_SERVER_PROJ="$REPO_ROOT/work/phone-deck/windows/PhoneDeck.Server/PhoneDeck.Server.csproj"
  WIN_SERVER_TESTS_PROJ="$REPO_ROOT/work/phone-deck/windows/PhoneDeck.Server.Tests/PhoneDeck.Server.Tests.csproj"
  WIN_CONSOLE_PROJ="$REPO_ROOT/work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj"
  WIN_OUT_SERVER="$OUT_FULL/windows/server"
  WIN_OUT_CONSOLE="$OUT_FULL/windows/console"
  WIN_TESTS_DIR="$REPORTS_DIR/windows-tests"
  mkdir -p "$WIN_OUT_SERVER" "$WIN_OUT_CONSOLE" "$WIN_TESTS_DIR"

  if [ "$SKIP_TESTS" = false ]; then
    echo "  [Windows] 运行接收端单元测试..."
    dotnet test "$WIN_SERVER_TESTS_PROJ" -c "$CONFIGURATION" --logger "trx;LogFileName=PhoneDeck.Server.Tests.trx" --results-directory "$WIN_TESTS_DIR"
  fi

  echo "  [Windows] 构建 PhoneDeck.Server ($CONFIGURATION)..."
  dotnet build "$WIN_SERVER_PROJ" -c "$CONFIGURATION"

  # WPF / WinForms 在非 Windows 平台仅支持跨平台编译（若 SDK 支持）
  if [[ "$OSTYPE" == "msys"* || "$OSTYPE" == "cygwin"* || "$OSTYPE" == "win32"* ]]; then
    echo "  [Windows] 构建 PhoneDeck.ControlCenter ($CONFIGURATION)..."
    dotnet build "$WIN_CONSOLE_PROJ" -c "$CONFIGURATION"

    echo "  [Windows] 发布自包含单文件 PhoneDeck.Server..."
    dotnet publish "$WIN_SERVER_PROJ" -c "$CONFIGURATION" -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$WIN_OUT_SERVER"

    echo "  [Windows] 发布自包含单文件 PhoneDeck.ControlCenter (含原生依赖)..."
    dotnet publish "$WIN_CONSOLE_PROJ" -c "$CONFIGURATION" -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$WIN_OUT_CONSOLE"

    # 单文件依赖检查
    if [ ! -f "$WIN_OUT_SERVER/PhoneDeck.Server.exe" ]; then
      echo "错误：未找到 $WIN_OUT_SERVER/PhoneDeck.Server.exe" >&2
      exit 1
    fi
    if [ ! -f "$WIN_OUT_CONSOLE/PhoneDeck.ControlCenter.exe" ]; then
      echo "错误：未找到 $WIN_OUT_CONSOLE/PhoneDeck.ControlCenter.exe" >&2
      exit 1
    fi
    # 检查是否有散落的 DLL
    if compgen -G "$WIN_OUT_CONSOLE/*.dll" > /dev/null; then
      echo "错误：ControlCenter 发布目录存在散落 dll，说明原生依赖未成功内嵌打包进单文件！" >&2
      exit 1
    fi
    echo "    ✓ Windows Server & ControlCenter 单文件发布校验通过"
  else
    echo "  [Windows] 非 Windows 主机环境，跳过 WPF 原生控制台单文件打包"
  fi
fi

# 2. Android 构建
if [ "$PLATFORM" = "All" ] || [ "$PLATFORM" = "Android" ]; then
  echo -e "\n>>> 执行 Android 平台构建与验证..."
  ANDROID_DIR="$REPO_ROOT/work/phone-deck/android"
  ANDROID_OUT="$OUT_FULL/android"
  ANDROID_REPORTS_DIR="$REPORTS_DIR/android"
  mkdir -p "$ANDROID_OUT" "$ANDROID_REPORTS_DIR"

  pushd "$ANDROID_DIR" > /dev/null
  chmod +x gradlew || true
  
  TASKS=(":app:assembleDebug")
  if [ "$SKIP_TESTS" = false ]; then
    TASKS+=(":app:testDebugUnitTest")
  fi
  TASKS+=(":app:lintDebug" ":app:assembleRelease")

  echo "  [Android] 执行 Gradle: ${TASKS[*]}..."
  ./gradlew "${TASKS[@]}" --no-daemon --console=plain
  popd > /dev/null

  cp "$ANDROID_DIR/app/build/outputs/apk/debug/app-debug.apk" "$ANDROID_OUT/PhoneDeck-debug.apk"
  
  RELEASE_APK=$(find "$ANDROID_DIR/app/build/outputs/apk/release" -name "*.apk" | head -n 1)
  if [ -n "$RELEASE_APK" ]; then
    cp "$RELEASE_APK" "$ANDROID_OUT/"
  fi

  if [ -d "$ANDROID_DIR/app/build/reports" ]; then
    cp -R "$ANDROID_DIR/app/build/reports/." "$ANDROID_REPORTS_DIR/"
  fi
  echo "    ✓ Android APK 与测试/Lint 报告已归档"
fi

# 3. macOS 构建
if [ "$PLATFORM" = "All" ] || [ "$PLATFORM" = "MacOS" ]; then
  echo -e "\n>>> 执行 macOS 平台构建与验证..."
  MAC_RECEIVER_PROJ="$REPO_ROOT/work/phone-deck/macos/PhoneDeck.Receiver/PhoneDeck.Receiver.csproj"
  MAC_TESTS_PROJ="$REPO_ROOT/work/phone-deck/macos/PhoneDeck.Receiver.Tests/PhoneDeck.Receiver.Tests.csproj"
  MAC_TESTS_DIR="$REPORTS_DIR/macos-tests"
  mkdir -p "$MAC_TESTS_DIR"

  if [ "$SKIP_TESTS" = false ]; then
    echo "  [macOS] 运行接收端单元测试..."
    dotnet test "$MAC_TESTS_PROJ" -c "$CONFIGURATION" --logger "trx;LogFileName=PhoneDeck.Receiver.Tests.trx" --results-directory "$MAC_TESTS_DIR"
  fi

  echo "  [macOS] 构建 PhoneDeck.Receiver ($CONFIGURATION)..."
  dotnet build "$MAC_RECEIVER_PROJ" -c "$CONFIGURATION"

  if [[ "$OSTYPE" == "darwin"* ]]; then
    echo "  [macOS] 构建 macOS App Bundle..."
    zsh "$REPO_ROOT/scripts/macos/Build-PhoneDeckReceiver.sh"
  fi
  echo "    ✓ macOS 构建与测试完成"
fi

echo -e "\n=================================================="
echo "PhoneDeck 开发构建与检查全部完成！"
echo "产物目录: $OUT_FULL"
echo "报告目录: $REPORTS_DIR"
echo "=================================================="
