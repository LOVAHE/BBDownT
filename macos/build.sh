#!/usr/bin/env bash
# 打包 BBDownT.app（macOS 应用）：BBDownT 原生程序 + Swift 窗口外壳
#
# 用法：
#   macos/build.sh                      编译 Apple 芯片版，输出到 macos/build/BBDownT.app
#   macos/build.sh --install            同上，并安装到 /Applications（正在运行的 BBDownT 有下载时会先弹框确认）
#   macos/build.sh --engine <路径>      直接用现成的 BBDownT 程序（例如 GitHub 构建产物），跳过编译
#   macos/build.sh --engine <arm64程序> --engine-x64 <x64程序> --zip --out <目录>
#                                       打包同时支持 Apple 芯片和 Intel 的通用版，并生成 BBDownT_macOS.zip（GitHub 工作流用）
#   macos/build.sh --universal          自己编译两种架构的 BBDownT 并打包通用版
#
# 本机没有 .NET 9 SDK 时会临时下载到临时目录，构建完删除。
# 运行时需要 ffmpeg（brew install ffmpeg）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd -P)"   # -P：经快捷方式（符号链接）调用时也能找到仓库根目录
SRC="${BBDOWNT_SRC:-$(cd "$HERE/.." && pwd)}"
OUT="$HERE/build"
INSTALL=0
ZIP=0
UNIVERSAL=0
ENGINE=""
ENGINE_X64=""
while [ $# -gt 0 ]; do
  case "$1" in
    --install) INSTALL=1 ;;
    --zip) ZIP=1 ;;
    --universal) UNIVERSAL=1 ;;
    --engine) ENGINE="${2:?--engine 需要路径}"; shift ;;
    --engine-x64) ENGINE_X64="${2:?--engine-x64 需要路径}"; UNIVERSAL=1; shift ;;
    --out) OUT="${2:?--out 需要目录}"; shift ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
  shift
done
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
APP="$OUT/BBDownT.app"

[ -f "$SRC/BBDownT/BBDownT.csproj" ] || { echo "找不到源码：$SRC" >&2; exit 1; }
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$SRC/BBDownT/BBDownT.csproj" | head -1)"
BUILD_NO="${GITHUB_RUN_NUMBER:-$(date +%Y%m%d%H%M)}"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---- 1) BBDownT 原生程序 ----
publish() { # $1 = osx-arm64 | osx-x64，输出到 $TMP/engine-$1/BBDownT
  if [ -z "${DOTNET:-}" ]; then
    if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^9\.'; then
      DOTNET="$(command -v dotnet)"
    else
      echo ">> 临时下载 .NET 9 SDK（构建完删除）"
      curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP/dotnet-install.sh"
      bash "$TMP/dotnet-install.sh" --channel 9.0 --install-dir "$TMP/dotnet" --no-path >/dev/null
      DOTNET="$TMP/dotnet/dotnet"
      export DOTNET_ROOT="$TMP/dotnet"
    fi
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_PACKAGES="${NUGET_PACKAGES:-$TMP/nuget}"
  fi
  echo ">> 编译 BBDownT $1（$(git -C "$SRC" log --oneline -1 2>/dev/null || echo 未知版本)）"
  "$DOTNET" publish "$SRC/BBDownT" -c Release -r "$1" -o "$TMP/engine-$1" | grep -E "error|warning CS|->" || true
  [ -x "$TMP/engine-$1/BBDownT" ] || { echo "BBDownT $1 编译失败" >&2; exit 1; }
}

if [ -n "$ENGINE" ]; then
  [ -x "$ENGINE" ] || { echo "找不到可执行的 BBDownT：$ENGINE" >&2; exit 1; }
  mkdir -p "$TMP/engine-osx-arm64" && cp "$ENGINE" "$TMP/engine-osx-arm64/BBDownT"
  echo ">> 使用现成的 BBDownT：$ENGINE"
else
  publish osx-arm64
fi
if [ "$UNIVERSAL" = 1 ]; then
  if [ -n "$ENGINE_X64" ]; then
    [ -x "$ENGINE_X64" ] || { echo "找不到可执行的 BBDownT（x64）：$ENGINE_X64" >&2; exit 1; }
    mkdir -p "$TMP/engine-osx-x64" && cp "$ENGINE_X64" "$TMP/engine-osx-x64/BBDownT"
  else
    publish osx-x64
  fi
  lipo -create "$TMP/engine-osx-arm64/BBDownT" "$TMP/engine-osx-x64/BBDownT" -output "$TMP/bbdownt-engine"
else
  cp "$TMP/engine-osx-arm64/BBDownT" "$TMP/bbdownt-engine"
fi
echo ">> 程序架构：$(lipo -archs "$TMP/bbdownt-engine")"

# ---- 2) 窗口外壳 ----
echo ">> 编译窗口外壳"
swiftc -swift-version 5 -O -target arm64-apple-macos13.0 "$HERE/main.swift" -o "$TMP/shell-arm64" \
  -framework Cocoa -framework WebKit
if [ "$UNIVERSAL" = 1 ]; then
  swiftc -swift-version 5 -O -target x86_64-apple-macos13.0 "$HERE/main.swift" -o "$TMP/shell-x64" \
    -framework Cocoa -framework WebKit
  lipo -create "$TMP/shell-arm64" "$TMP/shell-x64" -output "$TMP/BBDownT"
else
  cp "$TMP/shell-arm64" "$TMP/BBDownT"
fi

# ---- 3) 图标 ----
echo ">> 生成图标"
swift "$HERE/make-icon.swift" "$TMP/icon.png"
mkdir -p "$TMP/AppIcon.iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$TMP/icon.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$TMP/icon.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$TMP/AppIcon.iconset" -o "$TMP/AppIcon.icns"

# ---- 4) 组装 + 本地签名（ad-hoc，未经苹果公证） ----
echo ">> 组装 $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$TMP/BBDownT" "$APP/Contents/MacOS/BBDownT"
cp "$TMP/bbdownt-engine" "$APP/Contents/MacOS/bbdownt-engine"
cp "$TMP/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
sed -e "s/__VERSION__/${VERSION:-0}/" -e "s/__BUILD__/${BUILD_NO}/" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
codesign --force -s - "$APP/Contents/MacOS/bbdownt-engine"
codesign --force -s - "$APP"
codesign --verify --strict "$APP" && echo ">> 签名校验通过（本地签名）"

if [ "$ZIP" = 1 ]; then
  rm -f "$OUT/BBDownT_macOS.zip"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/BBDownT_macOS.zip"
  echo ">> 已生成 $OUT/BBDownT_macOS.zip（$(du -h "$OUT/BBDownT_macOS.zip" | cut -f1)）"
fi

if [ "$INSTALL" = 1 ]; then
  # 请正在运行的 BBDownT 退出；有下载在进行时它会弹框让用户决定，这里不等回复
  osascript -e 'ignoring application responses' -e 'tell application id "local.bbdownt.app" to quit' -e 'end ignoring' >/dev/null 2>&1 || true
  sleep 2
  rm -rf /Applications/BBDownT.app
  ditto "$APP" /Applications/BBDownT.app
  echo ">> 已安装到 /Applications/BBDownT.app"
  if pgrep -f "/Applications/BBDownT.app/Contents/MacOS/BBDownT" >/dev/null 2>&1; then
    echo ">> BBDownT 仍在运行（可能在下载或在等你确认退出）：新版下次打开时生效"
  fi
fi
echo ">> 完成：BBDownT ${VERSION:-?}"
if [ -z "${CI:-}" ]; then
  command -v ffmpeg >/dev/null 2>&1 || [ -x /opt/homebrew/bin/ffmpeg ] || echo "!! 没找到 ffmpeg，请先 brew install ffmpeg（合并音视频需要）"
fi
