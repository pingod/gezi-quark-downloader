#!/usr/bin/env bash
# GeZi（鸽子下载）— Linux 原生版启动脚本
#
# 用法：
#   ./gezi.sh              启动图形界面
#   ./gezi.sh --build      先重新编译再启动
#   ./gezi.sh --check      只做环境自检，不启动
#   ./gezi.sh --selftest   跑程序内置自检（不开界面）
#
# 说明：本脚本只负责找到运行时、选对图形后端、挑可写的配置/下载目录，
#       不修改你的任何系统配置。

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------- 颜色 ----------
if [ -t 1 ]; then
  C_OK=$'\e[32m'; C_ERR=$'\e[31m'; C_DIM=$'\e[2m'; C_END=$'\e[0m'
else
  C_OK=''; C_ERR=''; C_DIM=''; C_END=''
fi
ok()  { echo "${C_OK}✓${C_END} $*"; }
err() { echo "${C_ERR}✗${C_END} $*" >&2; }
dim() { echo "${C_DIM}  $*${C_END}"; }

# ============================================================
# 1. 运行时环境（必须在自检之前解析，否则自检会误判没有图形会话）
# ============================================================

# 开发用的 SDK 环境（构建时需要；运行自包含版不需要）
if [ -f "$HERE/../../.tools/env.sh" ]; then
  # shellcheck disable=SC1091
  source "$HERE/../../.tools/env.sh" 2>/dev/null || true
fi

# ---- Wayland socket 目录 ----
# ⚠️ 不能用 "${XDG_RUNTIME_DIR:-默认值}" 兜底：上面 source 的 env.sh 为了绕开
#    .NET 命名 mutex 的限制，把该变量指到了工作区目录。而 Wayland socket 在
#    /run/user/<uid>/ 下 —— 沿用它会让程序找不到 wayland-1，报 XOpenDisplay failed。
#    所以按「哪个目录里真的有 wayland socket」来选。
pick_runtime_dir() {
  local std="/run/user/$(id -u)"

  # ① 当前值里真有 socket → 用它
  if [ -n "${XDG_RUNTIME_DIR:-}" ] && [ -d "${XDG_RUNTIME_DIR}" ]; then
    if compgen -G "${XDG_RUNTIME_DIR}/wayland-*" >/dev/null 2>&1; then
      echo "${XDG_RUNTIME_DIR}"; return
    fi
  fi
  # ② 标准位置有 socket → 用它
  if compgen -G "${std}/wayland-*" >/dev/null 2>&1; then
    echo "$std"; return
  fi
  # ③ 都没有 → 仍用标准位置（X11 场景也要这个变量存在）
  echo "$std"
}
XDG_RUNTIME_DIR="$(pick_runtime_dir)"
export XDG_RUNTIME_DIR

# ---- Wayland display 名 ----
if [ -z "${WAYLAND_DISPLAY:-}" ]; then
  WAYLAND_DISPLAY="$(basename "$(compgen -G "${XDG_RUNTIME_DIR}/wayland-*" | grep -v '\.lock$' | head -1)" 2>/dev/null)"
  [ -n "${WAYLAND_DISPLAY}" ] && export WAYLAND_DISPLAY
fi

# ---- 配置目录：标准位置不可写时退回项目目录 ----
resolve_config_dir() {
  local cand
  for cand in "${GEZI_CONFIG_DIR:-}" \
              "${XDG_CONFIG_HOME:-$HOME/.config}/GeZi" \
              "$HERE/config"; do
    [ -z "$cand" ] && continue
    if mkdir -p "$cand" 2>/dev/null && [ -w "$cand" ]; then
      echo "$cand"; return
    fi
  done
  echo "$HERE/config"
}
GEZI_CONFIG_DIR="$(resolve_config_dir)"
export GEZI_CONFIG_DIR

# ---- 下载目录：~/Downloads 不可写时退回项目目录 ----
if [ -z "${GEZI_DOWNLOAD_DIR:-}" ]; then
  if [ -d "$HOME/Downloads" ] && [ -w "$HOME/Downloads" ]; then
    GEZI_DOWNLOAD_DIR="$HOME/Downloads"
  else
    GEZI_DOWNLOAD_DIR="$HERE/Downloads"
    mkdir -p "$GEZI_DOWNLOAD_DIR" 2>/dev/null
  fi
fi
export GEZI_DOWNLOAD_DIR

# ============================================================
# 2. 环境自检
# ============================================================
check_env() {
  local bad=0

  echo "环境自检："

  # .NET 运行时（自包含发布版不需要外部运行时）
  if [ -x "$HERE/dist/GeZi.Avalonia" ] && [ -f "$HERE/dist/libcoreclr.so" ]; then
    ok "自包含运行时（$HERE/dist）"
  elif [ -n "${DOTNET_ROOT:-}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
    ok ".NET 运行时：$DOTNET_ROOT"
  elif command -v dotnet >/dev/null 2>&1; then
    ok "系统 dotnet：$(dotnet --version 2>/dev/null || echo '?')"
  else
    err "找不到 .NET 运行时。先执行 ./gezi.sh --build 生成自包含版本。"
    bad=1
  fi

  # 图形会话：以 socket 是否存在为准
  local wl=""
  local f
  for f in "${XDG_RUNTIME_DIR}"/wayland-*; do
    [ -S "$f" ] || continue
    case "$f" in *.lock) continue ;; esac
    wl="$(basename "$f")"; break
  done

  if [ -n "$wl" ]; then
    ok "Wayland 会话（$wl @ $XDG_RUNTIME_DIR）"
  elif [ -n "${DISPLAY:-}" ] && { [ -d /tmp/.X11-unix ] || [ -f "${XAUTHORITY:-/nonexistent}" ]; }; then
    ok "X11 会话（$DISPLAY）"
  else
    err "没有可用的图形会话（既没有 Wayland socket，也没有 X11）。"
    dim "请在桌面终端里运行，而不是 SSH / 无图形环境。"
    bad=1
  fi

  # OpenSSL 3.5+（扫码登录通道需要后量子混合密钥交换）
  if command -v openssl >/dev/null 2>&1; then
    local v major minor
    v=$(openssl version 2>/dev/null | awk '{print $2}')
    major=${v%%.*}; minor=$(echo "$v" | cut -d. -f2)
    if [ "${major:-0}" -gt 3 ] || { [ "${major:-0}" -eq 3 ] && [ "${minor:-0}" -ge 5 ]; }; then
      ok "OpenSSL $v（支持 X25519MLKEM768）"
    else
      err "OpenSSL $v 偏低，扫码登录可能被拦（需要 3.5+）"
      bad=1
    fi
  else
    err "找不到 openssl 命令"
    bad=1
  fi

  # 中文字体
  if command -v fc-list >/dev/null 2>&1; then
    local n
    n=$(fc-list :lang=zh 2>/dev/null | wc -l)
    if [ "$n" -gt 0 ]; then
      ok "中文字体 $n 个"
    else
      err "没装中文字体，界面会显示成方块"; dim "安装：sudo pacman -S noto-fonts-cjk"; bad=1
    fi
  fi

  echo
  dim "配置目录：$GEZI_CONFIG_DIR"
  dim "下载目录：$GEZI_DOWNLOAD_DIR"

  return $bad
}

# ============================================================
# 3. 构建
# ============================================================
build() {
  if ! command -v dotnet >/dev/null 2>&1; then
    err "找不到 dotnet SDK，无法编译。"
    exit 1
  fi
  echo "编译中（首次需要联网还原 NuGet 包）…"
  ( cd "$HERE/.." && dotnet publish src/GeZi.Avalonia/GeZi.Avalonia.csproj \
      -c Release -r linux-x64 --self-contained true -o "$HERE/dist" )
  ok "编译完成：$HERE/dist"
}

# ============================================================
# 4. 参数分派
# ============================================================
case "${1:-}" in
  --check)
    check_env && { echo; echo "环境没问题，可以启动。"; exit 0; }
    exit 1
    ;;
  --build)
    build
    exit $?
    ;;
  --help|-h)
    sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
esac

# ============================================================
# 5. 启动
# ============================================================
check_env || echo

# 定位可执行文件：优先自包含发布版，其次开发构建
APP="$HERE/dist/GeZi.Avalonia"
if [ ! -x "$APP" ]; then
  DEV="$HERE/../src/GeZi.Avalonia/bin/Release/net8.0/GeZi.Avalonia"
  if [ -x "$DEV" ]; then
    APP="$DEV"
    dim "使用开发构建：$DEV"
  else
    err "还没编译。先运行：$0 --build"
    exit 1
  fi
fi

# 自包含版不需要 DOTNET_ROOT；框架依赖版需要
if [ ! -f "$(dirname "$APP")/libcoreclr.so" ] && [ -n "${DOTNET_ROOT:-}" ]; then
  export DOTNET_ROOT
fi

exec "$APP" "$@"
