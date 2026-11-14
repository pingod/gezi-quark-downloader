#!/usr/bin/env bash
# GeZi Linux AppImage 打包（零第三方依赖，只用 coreutils + appimagetool）。
#
# 输入是 `dotnet publish` 的自包含输出目录，输出是单文件 .AppImage + .sha256。
#
# 用法：
#   tools/package_appimage.sh --publish-dir publish --out artifacts
#   tools/package_appimage.sh --publish-dir publish --out artifacts --version 2.5.0
#
# 环境变量：
#   APPIMAGETOOL  指定 appimagetool（.AppImage 或解包后的 AppRun 均可）。
#                 未指定时：PATH 里有就用，否则下载钉死版本的官方 AppImage 并校验 SHA-256。
#
# 退出码：0 = 成功；1 = 缺文件 / 工具失败。
#
# 设计取舍：
#   · **不带 PDB**。PDB 会记录编译机的源码路径（见 tools/package_release.py 的长注释），
#     发布包里不该出现；AppImage 里同样剔除。
#   · **不用 FUSE 校验**：打包机上往往没有 fuse 权限，appimagetool 自己也是 AppImage，
#     统一走 APPIMAGE_EXTRACT_AND_RUN=1 解包执行，结果一致且不依赖 /dev/fuse。
#   · **不加 zsync/更新信息**：更新信息需要稳定的下载 URL 与 zsyncmake，
#     发版流程里没人验证过，先不给用户一个会静默失败的功能。

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$HERE")"

PUBLISH_DIR="publish"
OUT_DIR="artifacts"
VERSION=""

# 钉死版本的 appimagetool：连续构建会变（同一 URL 内容不同），
# 发布工具链必须可复现，所以固定 tag + 校验哈希。
APPIMAGETOOL_VERSION="1.9.1"
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/${APPIMAGETOOL_VERSION}/appimagetool-x86_64.AppImage"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"

die() { echo "ERROR $*" >&2; exit 1; }
log() { echo "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --publish-dir) PUBLISH_DIR="${2:-}"; shift 2 ;;
    --out)         OUT_DIR="${2:-}";     shift 2 ;;
    --version)     VERSION="${2:-}";     shift 2 ;;
    -h|--help)     sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             die "未知参数: $1" ;;
  esac
done

# ---------- 版本号：与 Avalonia 项目一致，避免包名和程序内版本各说各话 ----------
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*:\1:p' \
    "$REPO_ROOT/src/GeZi.Avalonia/GeZi.Avalonia.csproj" | head -1)"
  [ -n "$VERSION" ] || die "无法从 src/GeZi.Avalonia/GeZi.Avalonia.csproj 读到 <Version>，用 --version 指定"
fi

# ---------- 输入校验 ----------
[ -d "$PUBLISH_DIR" ] || die "发布目录不存在: $PUBLISH_DIR（先 dotnet publish）"
[ -x "$PUBLISH_DIR/GeZi.Avalonia" ] || die "缺少可执行文件: $PUBLISH_DIR/GeZi.Avalonia"
[ -f "$PUBLISH_DIR/libcoreclr.so" ] || die "缺少自包含运行时: $PUBLISH_DIR/libcoreclr.so（--self-contained true？）"
[ -f "$PUBLISH_DIR/GeZi.Avalonia.dll" ] || die "缺少托管程序集: $PUBLISH_DIR/GeZi.Avalonia.dll"

ICON="$REPO_ROOT/src/GeZi.Avalonia/Assets/app-logo.png"
[ -f "$ICON" ] || die "缺少图标: $ICON"

OUT_DIR="$(mkdir -p "$OUT_DIR" && cd "$OUT_DIR" && pwd)"
PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"

APP_NAME="GeZi"
APP_ID="gezi"
EXEC_NAME="GeZi.Avalonia"
APPDIR_NAME="GeZi.AppDir"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

APPDIR="$WORK/$APPDIR_NAME"
mkdir -p "$APPDIR/usr/bin" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/licenses/$APP_NAME"

# ---------- 1) 程序本体 ----------
# PDB 不进包（含编译机路径）；其余运行时文件全部保留。
( cd "$PUBLISH_DIR" && find . -type f ! -name '*.pdb' -print0 | tar -cf - --null -T - ) \
  | tar -xf - -C "$APPDIR/usr/bin"
chmod 0755 "$APPDIR/usr/bin/$EXEC_NAME"

# ---------- 2) 图标 ----------
# 根目录的 <icon>.png 与 .DirIcon 是 AppImage 规范的硬要求（桌面环境靠它显示图标）。
install -m 0644 "$ICON" "$APPDIR/$APP_ID.png"
ln -sf "$APP_ID.png" "$APPDIR/.DirIcon"
install -m 0644 "$ICON" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"

# ---------- 3) 桌面入口 ----------
cat > "$APPDIR/$APP_ID.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=鸽子下载
Name[en]=GeZi Downloader
GenericName=网盘下载器
Comment=夸克 / UC / 阿里云盘 / 迅雷云盘 多网盘下载器
Exec=$EXEC_NAME
Icon=$APP_ID
Terminal=false
Categories=Network;FileTransfer;
Keywords=download;quark;uc;aliyun;xunlei;网盘;下载;
StartupNotify=true
X-AppImage-Version=$VERSION
EOF
install -m 0644 "$APPDIR/$APP_ID.desktop" "$APPDIR/usr/share/applications/$APP_ID.desktop"

# ---------- 4) AppRun ----------
# 用 APPDIR（AppImage 运行时提供）而不是 $0 的 dirname：mount 路径里可能含空格，
# 且用 --appimage-extract 解包后直接跑 AppRun 时 $APPDIR 不存在，所以两条路都要能用。
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
set -e
if [ -n "${APPDIR:-}" ] && [ -d "$APPDIR/usr/bin" ]; then
  HERE="$APPDIR"
else
  HERE="$(cd "$(dirname "$0")" && pwd)"
fi
exec "$HERE/usr/bin/GeZi.Avalonia" "$@"
EOF
chmod 0755 "$APPDIR/AppRun"

# ---------- 5) 合规文件 ----------
for f in LICENSE THIRD-PARTY-NOTICES.md; do
  [ -f "$REPO_ROOT/$f" ] || die "缺少合规文件: $f"
  install -m 0644 "$REPO_ROOT/$f" "$APPDIR/usr/share/licenses/$APP_NAME/$f"
done

# ---------- 6) appimagetool ----------
resolve_appimagetool() {
  if [ -n "${APPIMAGETOOL:-}" ]; then
    echo "$APPIMAGETOOL"; return
  fi
  if command -v appimagetool >/dev/null 2>&1; then
    command -v appimagetool; return
  fi
  local cache="${XDG_CACHE_HOME:-$HOME/.cache}/gezi-appimage"
  local tool="$cache/appimagetool-${APPIMAGETOOL_VERSION}-x86_64.AppImage"
  if [ ! -f "$tool" ]; then
    mkdir -p "$cache"
    log "下载 appimagetool ${APPIMAGETOOL_VERSION} …" >&2
    curl -fsSL --retry 3 -o "$tool.part" "$APPIMAGETOOL_URL" \
      || die "下载 appimagetool 失败: $APPIMAGETOOL_URL"
    mv "$tool.part" "$tool"
  fi
  local got
  got="$(sha256sum "$tool" | cut -d' ' -f1)"
  [ "$got" = "$APPIMAGETOOL_SHA256" ] \
    || die "appimagetool 校验失败: 期望 $APPIMAGETOOL_SHA256 实际 $got"
  chmod 0755 "$tool"
  echo "$tool"
}

TOOL="$(resolve_appimagetool)"
[ -e "$TOOL" ] || die "appimagetool 不可用: $TOOL"
# AppImage 形式的 appimagetool 必须有执行位；下载/缓存来的可能没有。
[ -x "$TOOL" ] || chmod 0755 "$TOOL" 2>/dev/null || die "appimagetool 不可执行: $TOOL"

APPIMAGE="$OUT_DIR/${APP_NAME}-v${VERSION}-x86_64.AppImage"

# APPIMAGE_EXTRACT_AND_RUN=1：不挂 FUSE，直接解包执行（CI / 容器里没有 /dev/fuse）。
# ARCH 显式给出，否则 appimagetool 会从 uname 猜，在 qemu/多架构 runner 上会猜错。
log "生成 AppImage（appimagetool: $TOOL）…"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" \
  --no-appstream \
  --comp zstd \
  "$APPDIR" "$APPIMAGE" 2>&1 | sed 's/^/  /'

[ -f "$APPIMAGE" ] || die "appimagetool 没有产出 $APPIMAGE"
chmod 0755 "$APPIMAGE"

# ---------- 7) 自检 + 哈希 ----------
# 结构自检：解包后确认关键文件都在、可执行、链接可解析。
# 真正的界面启动留给有图形会话的机器（CI runner 没有图形栈，起界面必然失败）。
EXTRACT="$WORK/extract"
mkdir -p "$EXTRACT"
( cd "$EXTRACT" && "$APPIMAGE" --appimage-extract >/dev/null )
S="$EXTRACT/squashfs-root"
[ -x "$S/usr/bin/$EXEC_NAME" ] || die "解包后缺少可执行文件"
[ -f "$S/usr/bin/libcoreclr.so" ] || die "解包后缺少 libcoreclr.so"
[ -f "$S/$APP_ID.desktop" ] || die "解包后缺少 desktop 文件"
[ -e "$S/.DirIcon" ] || die "解包后缺少 .DirIcon"
file -b "$S/usr/bin/$EXEC_NAME" | grep -q 'ELF 64-bit' || die "可执行文件不是 x86-64 ELF"
# ldd 报 "not found" 时退出码非 0；这里反过来断言「没有 not found」
! ldd "$S/usr/bin/$EXEC_NAME" | grep -q 'not found' || die "动态链接依赖缺失"

SIZE="$(du -h "$APPIMAGE" | cut -f1)"
DIGEST="$(sha256sum "$APPIMAGE" | cut -d' ' -f1)"
( cd "$OUT_DIR" && echo "$DIGEST  $(basename "$APPIMAGE")" > "$(basename "$APPIMAGE").sha256" )

log "写出 $APPIMAGE ($SIZE)"
log "SHA-256 = $DIGEST"
