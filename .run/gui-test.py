#!/usr/bin/env python3
"""GeZi GUI 自动化测试（在 wstream 隔离沙箱里跑，不碰用户桌面）

用法：
    ./gui-test.py                # 全套
    ./gui-test.py --keep         # 测完不关沙箱（方便人工接着看）

⚠️ 两个必须记住的坑（都实际踩过）：
  1. **必须传实际屏幕尺寸**给 WStream(width=..., height=...)。
     默认是 1280x720，而沙箱是 1600x1000 —— 不传的话所有坐标会偏移约 25%，
     表现为"点了按钮完全没反应"，极难排查。
  2. **沙箱进程会随调用结束被回收**。所以启动沙箱、启动应用、操作、截图
     必须在**同一次进程**里完成，不能分多次跑。
"""

import json
import os
import subprocess
import sys
import time
import urllib.request

WSTREAM = "/home/pavia/githome/wstream"
GEZI = "/home/pavia/githome/gezi-quark-downloader"
BIN = os.path.join(GEZI, ".run/test-dist/GeZi.Avalonia")
CFG = os.path.join(GEZI, ".run/test-cfg")
DL = os.path.join(GEZI, ".run/test-dl")
SHOTS = os.path.join(GEZI, ".run/shots")

SCREEN_W, SCREEN_H = 1600, 1000

sys.path.insert(0, os.path.join(WSTREAM, "clients"))

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    mark = "✓" if ok else "✗"
    print(f"  {mark} {name}" + (f"  [{detail}]" if detail else ""))


def windows():
    with urllib.request.urlopen("http://127.0.0.1:8765/windows", timeout=5) as r:
        return json.load(r)["windows"]


def find(app_id):
    return next((w for w in windows() if w["app_id"] == app_id), None)


def start_sandbox():
    subprocess.run([os.path.join(WSTREAM, "scripts/start.sh")],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    time.sleep(7)


def stop_sandbox():
    subprocess.run([os.path.join(WSTREAM, "scripts/start.sh"), "stop"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)


def launch_app():
    root = "/tmp/wstream"
    with open(os.path.join(root, "wl_name")) as f:
        wl = f.read().strip()

    os.makedirs(CFG, exist_ok=True)
    os.makedirs(DL, exist_ok=True)

    env = {
        "XDG_RUNTIME_DIR": f"{root}/run",
        "WAYLAND_DISPLAY": wl,
        "GEZI_CONFIG_DIR": CFG,
        "GEZI_DOWNLOAD_DIR": DL,
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "HOME": os.environ.get("HOME", "/tmp"),
    }

    # 继承代理变量 —— 应用会在启动时按它们自动启用代理。
    # 漏传的话测试环境和真实环境不一致（真实环境里用户是有代理的）。
    for k in ("http_proxy", "https_proxy", "all_proxy", "no_proxy",
              "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"):
        if k in os.environ:
            env[k] = os.environ[k]

    subprocess.Popen(["setsid", BIN], env=env,
                     stdout=open(f"{GEZI}/.run/test-app.log", "w"),
                     stderr=subprocess.STDOUT,
                     start_new_session=True)
    time.sleep(14)


def app_log():
    path = os.path.join(CFG, "GeZi.log")
    if not os.path.exists(path):
        return ""
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def main():
    keep = "--keep" in sys.argv

    print("=" * 56)
    print("GeZi GUI 自动化测试（wstream 隔离沙箱）")
    print("=" * 56)

    start_sandbox()

    try:
        # 清掉上次的日志，避免干扰判断
        log_path = os.path.join(CFG, "GeZi.log")
        if os.path.exists(log_path):
            os.remove(log_path)

        launch_app()

        # ---------- 1. 启动 ----------
        g = find("GeZi.Avalonia")
        check("应用启动并出窗口", g is not None,
              f"{g['width']}x{g['height']}" if g else "未出现")
        if not g:
            return finish(keep)

        from wstream_client import WStream

        # ⚠️ 关键：传实际屏幕尺寸
        with WStream(width=SCREEN_W, height=SCREEN_H) as ws:
            ws.wait_ready()
            ws.take()

            # ---------- 2. 主界面元素 ----------
            log = app_log()
            check("启动日志正常", "就绪" in log, "含『就绪』")
            check("代理自动检测生效", "自动启用代理" in log,
                  "已启用" if "自动启用代理" in log else "未触发（无代理时正常）")

            # ---------- 3. 平台选择器 ----------
            # 按钮在第二行（y≈75），从左往右扫
            BTN_Y = 75
            picker = None
            for dx in (90, 130, 170, 210, 250):
                ws.click(g["x"] + dx, g["y"] + BTN_Y)
                time.sleep(2.5)
                picker = next((w for w in windows() if w["title"] == "选择网盘"), None)
                if picker:
                    print(f"    （我的网盘按钮在窗口内 x={dx}, y={BTN_Y}）")
                    break

            check("『我的网盘』弹出平台选择器", picker is not None,
                  "已弹出" if picker else "未弹出")

            if picker:
                ws.snapshot(os.path.join(SHOTS, "gui-picker.jpg"))
                check("平台选择器截图", True, "gui-picker.jpg")

                # 关掉选择器（点「取消」）。
                # 位置按**实测比例**算（从截图量得：x≈74.5%，y≈91.3%），
                # 因为平铺合成器下对话框的 width/height 与实际可视区域不一定一致。
                print(f"    选择器几何: {picker['x']},{picker['y']} "
                      f"{picker['width']}x{picker['height']}")
                ws.click(picker["x"] + int(picker["width"] * 0.745),
                         picker["y"] + int(picker["height"] * 0.913))
                time.sleep(1.5)

                still = next((w for w in windows() if w["title"] == "选择网盘"), None)
                check("选择器可关闭", still is None, "已关闭" if still is None else "仍开着")

                # 如果还开着，用 Esc 兜底
                if still:
                    ws.press("esc")
                    time.sleep(1.5)
                    still = next((w for w in windows() if w["title"] == "选择网盘"), None)
                    check("Esc 可关闭选择器", still is None,
                          "已关闭" if still is None else "仍开着")

            # ---------- 4. 设置窗口 ----------
            # 按钮位置随窗口宽度变化（WrapPanel 会换行），所以多试几个候选点。
            # 从右往左扫，命中即停。
            settings = None
            for dx in (208, 180, 235, 160):
                ws.click(g["x"] + dx, g["y"] + BTN_Y)
                time.sleep(2.5)
                settings = next((w for w in windows() if w["title"] == "设置"), None)
                if settings:
                    print(f"    （设置按钮在窗口内 x={dx}）")
                    break

            check("『设置』窗口可打开", settings is not None,
                  "已打开" if settings else "未打开")
            if settings:
                ws.snapshot(os.path.join(SHOTS, "gui-settings.jpg"))
                ws.click(settings["x"] + settings["width"] // 2,
                         settings["y"] + settings["height"] - 30)   # 取消
                time.sleep(1)

            # ---------- 5. 无崩溃 ----------
            time.sleep(1)
            log = app_log()
            crashed = "Unhandled exception" in log or "启动失败" in log
            check("运行期间无未处理异常", not crashed,
                  "有异常" if crashed else "干净")

            g2 = find("GeZi.Avalonia")
            check("应用仍在运行", g2 is not None, "存活" if g2 else "已退出")

    finally:
        finish(keep)


def finish(keep):
    passed = sum(1 for _, ok, _ in results if ok)
    failed = len(results) - passed

    print("-" * 56)
    print(f"通过 {passed} 项，失败 {failed} 项")

    if not keep:
        stop_sandbox()
        print("沙箱已关闭")

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
