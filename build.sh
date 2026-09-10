#!/usr/bin/env bash
# PhoneDeck 统一开发构建便捷入口：薄包装到 pwsh scripts/build.ps1
# 支持任意 cwd、含中文/空格路径；非零退出码透传；选项使用 PowerShell 统一名字。

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="$SCRIPT_DIR/scripts/build.ps1"

map_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -Platform|--platform|-p)
      map_args+=(-Platform "$2"); shift 2 ;;
    -Configuration|--configuration|-c)
      map_args+=(-Configuration "$2"); shift 2 ;;
    -OutputDir|--output-dir|-o)
      map_args+=(-OutputDir "$2"); shift 2 ;;
    -Clean|--clean)
      map_args+=(-Clean); shift ;;
    -SkipTests|--skip-tests)
      map_args+=(-SkipTests); shift ;;
    -h|--help)
      echo "用法: $0 [-Platform All|Windows|Android|MacOS] [-Configuration Release|Debug] [-OutputDir 路径] [-Clean] [-SkipTests]"
      echo "也接受 --platform/--configuration/--output-dir/--clean/--skip-tests，会映射为 PowerShell 参数名。"
      exit 0 ;;
    *)
      map_args+=("$1"); shift ;;
  esac
done

exec pwsh -NoProfile -File "$TARGET" "${map_args[@]}"
