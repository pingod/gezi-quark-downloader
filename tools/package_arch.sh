#!/usr/bin/env bash
# GeZi Linux Arch Linux 包（.pkg.tar.zst）打包。
#
# 输入是 `dotnet publish` 的自包含输出目录，输出是 pacman 可直接
# `pacman -U` 安装的包 + .sha256：
#   gezi-downloader-<版本>-1-x86_64.pkg.tar.zst
#
# 用法：
#   tools/package_arch.sh --publish-dir publish --out artifacts
#   tools/package_arch.sh --publish-dir publish --out artifacts --version 2.5.2
#
# 依赖：
#   · 本机是 Arch（有 makepkg）→ 直接调用；
#   · 否则有 docker → 用 archlinux:base-devel 容器跑 makepkg（CI 的 ubuntu runner 走这条）。
#   没有 makepkg 也没有 docker 时直接失败，不产出「看起来像包」的 tar。
#
# 退出码：0 = 成功；1 = 缺文件 / 工具失败。

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$HERE")"

PUBLISH_DIR="publish"
OUT_DIR="artifacts"
VERSION=""
CONTAINER_IMAGE="${ARCH_PKG_IMAGE:-archlinux:base-devel}"

die() { echo "ERROR $*" >&2; exit 1; }
log() { echo "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --publish-dir) PUBLISH_DIR="${2:-}"; shift 2 ;;
    --out)         OUT_DIR="${2:-}";     shift 2 ;;
    --version)     VERSION="${2:-}";     shift 2 ;;
    -h|--help)     sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             die "未知参数: $1" ;;
  esac
done

# ---------- 版本号：与 Avalonia 项目一致 ----------
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*:\1:p' \
    "$REPO_ROOT/src/GeZi.Avalonia/GeZi.Avalonia.csproj" | head -1)"
  [ -n "$VERSION" ] || die "无法从 src/GeZi.Avalonia/GeZi.Avalonia.csproj 读到 <Version>"
fi

# ---------- 输入校验 ----------
[ -d "$PUBLISH_DIR" ] || die "发布目录不存在: $PUBLISH_DIR（先 dotnet publish）"
[ -x "$PUBLISH_DIR/GeZi.Avalonia" ] || die "缺少可执行文件: $PUBLISH_DIR/GeZi.Avalonia"
[ -f "$PUBLISH_DIR/libcoreclr.so" ] || die "缺少自包含运行时: $PUBLISH_DIR/libcoreclr.so（--self-contained true？）"

ICON="$REPO_ROOT/src/GeZi.Avalonia/Assets/app-logo.png"
[ -f "$ICON" ] || die "缺少图标: $ICON"
for f in LICENSE THIRD-PARTY-NOTICES.md; do
  [ -f "$REPO_ROOT/$f" ] || die "缺少合规文件: $f"
done

OUT_DIR="$(mkdir -p "$OUT_DIR" && cd "$OUT_DIR" && pwd)"
PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---------- 组装 makepkg 工作目录 ----------
# makepkg 只认 $startdir/PKGBUILD；publish 与合规文件都放到同级，
# PKGBUILD 通过 $startdir 引用它们（不走 source=()，因为输入已经构建好了）。
cp -a "$PUBLISH_DIR" "$WORK/publish"
install -m 0644 "$REPO_ROOT/LICENSE" "$WORK/LICENSE"
install -m 0644 "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$WORK/THIRD-PARTY-NOTICES.md"
install -m 0644 "$ICON" "$WORK/gezi.png"
sed "s/@VERSION@/$VERSION/" "$HERE/arch/PKGBUILD" > "$WORK/PKGBUILD"

cat > "$WORK/gezi.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=鸽子下载
Name[en]=GeZi Downloader
GenericName=网盘下载器
Comment=夸克 / UC / 阿里云盘 / 迅雷云盘 多网盘下载器
Exec=gezi-downloader
Icon=gezi
Terminal=false
Categories=Network;FileTransfer;
Keywords=download;quark;uc;aliyun;xunlei;网盘;下载;
StartupNotify=true
EOF

# ---------- makepkg ----------
# --nodeps：包的 depends 是**目标机**要装的东西；打包机不需要先装一遍 GUI 库。
# --nocheck：PKGBUILD 没有 check()，显式跳过以免未来的默认行为变化。
run_makepkg_local() {
  PKGDEST="$OUT_DIR" makepkg --force --nodeps --nocheck --noconfirm
}

run_makepkg_container() {
  command -v docker >/dev/null 2>&1 || die "既没有 makepkg 也没有 docker，无法生成 Arch 包"
  log "用容器 $CONTAINER_IMAGE 运行 makepkg …"
  # 注意：不能 chown 挂载进来的宿主机目录 —— 会把 $WORK 变成 builder 所有，
  # 脚本退出时的 rm -rf 就会 "Operation not permitted"。
  # 所以 /seed 只读挂载，容器内复制一份给 builder 用。
  docker run --rm \
    -v "$WORK:/seed:ro" \
    -v "$OUT_DIR:/out" \
    "$CONTAINER_IMAGE" \
    bash -euc '
      pacman -Sy --noconfirm --needed fakeroot zstd >/dev/null
      useradd -m -s /bin/bash builder
      cp -a /seed /build
      chown -R builder /build
      su builder -c "cd /build && PKGDEST=/build/out makepkg --force --nodeps --nocheck --noconfirm"
      cp /build/out/*.pkg.tar.zst /out/
    '
}

if command -v makepkg >/dev/null 2>&1; then
  ( cd "$WORK" && run_makepkg_local )
else
  run_makepkg_container
fi

# ---------- 自检 + 哈希 ----------
PKG="$OUT_DIR/gezi-downloader-${VERSION}-1-x86_64.pkg.tar.zst"
[ -f "$PKG" ] || PKG="$(find "$OUT_DIR" -maxdepth 1 -name "gezi-downloader-${VERSION}-*.pkg.tar.zst" -print -quit)"
[ -f "$PKG" ] || die "makepkg 没有产出 gezi-downloader-${VERSION}-*.pkg.tar.zst"

# 包内必须有关键文件，否则装上也起不来。
# 列表工具：Arch 自带 bsdtar；ubuntu runner 通常有 zstd，退化成 zstd -dc | tar -t。
list_pkg() {
  if command -v bsdtar >/dev/null 2>&1; then
    bsdtar -tf "$1"
  elif command -v zstd >/dev/null 2>&1; then
    zstd -dc "$1" | tar -tf -
  else
    die "需要 bsdtar 或 zstd 来检查包内容"
  fi
}
LIST="$(list_pkg "$PKG")"
for want in opt/gezi/GeZi.Avalonia opt/gezi/libcoreclr.so \
            usr/bin/gezi-downloader usr/share/applications/gezi.desktop \
            usr/share/licenses/gezi-downloader/LICENSE; do
  printf '%s\n' "$LIST" | grep -qx "$want" || die "包内缺少: $want"
done
if printf '%s\n' "$LIST" | grep -q '\.pdb$'; then
  die "包内不应包含 PDB"
fi

DIGEST="$(sha256sum "$PKG" | cut -d' ' -f1)"
( cd "$OUT_DIR" && echo "$DIGEST  $(basename "$PKG")" > "$(basename "$PKG").sha256" )

log "写出 $PKG ($(du -h "$PKG" | cut -f1))"
log "SHA-256 = $DIGEST"