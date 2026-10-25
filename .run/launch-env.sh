#!/bin/sh
# 由 Hyprland 直接 exec（脱离 agent 沙箱生命周期）
export XDG_RUNTIME_DIR=/run/user/1000
export WAYLAND_DISPLAY=wayland-1
export GEZI_CONFIG_DIR=/home/pavia/githome/.tools/xdg/config/GeZi
export GEZI_DOWNLOAD_DIR=/home/pavia/githome/gezi-quark-downloader/.run/Downloads
exec /home/pavia/githome/gezi-quark-downloader/.run/dist/GeZi.Avalonia \
  > /home/pavia/githome/gezi-quark-downloader/.run/shots/app.log 2>&1
