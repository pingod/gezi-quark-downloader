# GeZi Linux 原生版

把原本 **Windows 独占** 的鸽子下载（WPF + .NET Framework 4.8）改造成**在 Linux 上原生运行**的版本。

> 不是 Wine，不是虚拟机 —— 是真正编译成 Linux ELF 的 .NET 8 程序，直接跑在 Wayland 上。

---

## 怎么跑起来

```bash
cd ~/githome/gezi-quark-downloader/.run

./gezi.sh --check    # 环境自检（推荐先跑一次）
./gezi.sh            # 启动
```

首次运行前如果还没编译过：

```bash
./gezi.sh --build    # 编译出自包含版本（约 95MB，不依赖系统 dotnet）
./gezi.sh
```

启动后：粘贴夸克分享链接 → 点「解析」→ 勾选文件 → 「下载选中文件」。

---

## 为什么必须改造

原项目 README 写得很明白：

> ⚠️ **本软件仅支持 Windows**，不支持 Linux / macOS。
> （UI 基于 WPF、运行时为 .NET Framework，两者均为 Windows 独占。）

WPF 的渲染依赖 DirectX + User32，没有 Linux 实现，**没有任何办法在 Linux 上原生运行 WPF**。
所以改造思路是：**核心逻辑保留，UI 层重写**。

---

## 改造方案

```
                    ┌─────────────────────────────┐
                    │      GeZi.Core（共享）       │
                    │  下载引擎 / 夸克 API / 解析   │
                    │  双目标：net48 + net8.0      │
                    └──────────┬──────────────────┘
                               │
                ┌──────────────┴──────────────┐
                │                             │
    ┌───────────▼──────────┐     ┌────────────▼───────────┐
    │   src/GeZi（WPF）     │     │  src/GeZi.Avalonia     │
    │   Windows 正式产物     │     │  Linux/macOS 原生       │
    │   保持不变            │     │  本次新增               │
    └──────────────────────┘     └────────────────────────┘
```

这与仓库里 `UZIP2` 项目已有的做法一致（跨平台 Core + Avalonia Linux 界面，WPF 仍作 Windows 产物）。

---

## Core 的 4 处平台改造

`GeZi.Core` 原本只能在 Windows 上编译。逐项替换后用 `#if NET8_0_OR_GREATER` 分流，
**net48 分支保持原样，Windows 行为零回归**。

| # | 原实现 | net8.0 新实现 | 文件 |
|---|---|---|---|
| 1 | `JavaScriptSerializer` | `System.Text.Json` + 形状适配 | `Support/JsonCompat.cs` |
| 2 | Windows DPAPI | AES-256-GCM + 本机密钥文件 | `Support/SecretProtector.cs` |
| 3 | GDI+（`System.Drawing`） | SkiaSharp + 纯托管像素管线 | `Support/ImageLoader.cs`、`Support/QrDecoder.cs` |
| 4 | `libssl-3-x64.dll` | 系统 `libssl.so.3` | `Support/OpenSslTls.cs` |

### 1. JSON —— 关键是"形状"要一致

全项目按 `Dictionary<string,object>` / `object[]` 的形状取值（`AsDict` / `AsLong` 等）。
如果直接改用 `JsonElement`，平台差异会扩散到每一个取值点。

所以 `JsonCompat` 只做一件事：**在 net8 上把 JSON 转成和 `JavaScriptSerializer` 一模一样的对象图**，
业务代码一行没改。数值映射刻意对齐（整数 → `int`，放不下 → `long`，小数 → `decimal`）。

### 2. 密钥加密 —— DPAPI 不存在了

Linux 没有 DPAPI。改为 AES-256-GCM，密钥首次使用时随机生成到
`~/.config/GeZi/secret.key`（权限 0600）。

安全定位与 DPAPI 相当：能读你用户目录的人本来也能读配置文件，
这里防的是「配置被误同步/误拷到别处」。密钥丢失 → 按未登录处理，不会崩。

### 3. 二维码解码 —— 顺手改得更好

原实现用 GDI+ 的 `Bitmap`/`Graphics` 做缩放、灰度、二值化。这套 API 在 .NET 6+ 的
Linux 上直接抛 `PlatformNotSupportedException`。

改造时做了个额外决定：**整条图像管线重写成纯托管数组操作**，ZXing 侧走
`RGBLuminanceSource`（它只吃字节数组，天然跨平台）。

好处不只是"能跑"——net48 和 net8.0 现在跑的是**同一份图像处理代码**，
不再有"Windows 上能用、Linux 上行为不同"的分裂。

### 4. OpenSSL —— 这次是运气好

原项目为了过 `pan.quark.cn` 的 TLS 指纹墙，内置了 OpenSSL 3.5.7，
因为该站 ALB 按 ClientHello 的 `supported_groups` 扩展大小过滤（阈值约 1600 字节），
只有 3.5+ 默认开启的 X25519MLKEM768 混合密钥交换够大。

本机系统 OpenSSL 是 **3.6.5**，本身就满足条件，所以 net8 分支直接
`DllImport("libssl.so.3")` 即可 —— 不用再捆一份 .so。

实测验证：

```
$ echo | openssl s_client -connect pan.quark.cn:443 -groups X25519MLKEM768
Verify return code: 0 (ok)
```

---

## 新增的界面层

`src/GeZi.Avalonia/`，Avalonia 12.1.4，约 900 行：

| 文件 | 作用 |
|---|---|
| `Program.cs` | 入口。**Wayland 优先**，无 Wayland 才退回 X11 |
| `App.axaml(.cs)` | 应用与 Fluent 主题 |
| `MainWindow.axaml(.cs)` | 主界面：解析 / 文件列表 / 下载任务 / 日志 |
| `LoginWindow.axaml(.cs)` | 粘贴 Cookie 登录 |
| `TaskRow.cs` | 界面数据模型（`INotifyPropertyChanged`） |
| `SettingsStore.cs` | 设置持久化到 `~/.config/GeZi/settings.json` |

### 一个必须说明的启动细节

原本用 `UsePlatformDetect()`，结果直接起不来：

```
启动失败: XOpenDisplay failed
```

原因：本机 `$DISPLAY=:0` 存在，但 `/tmp/.X11-unix/` 是空的（没有 Xwayland 在跑）。
`UsePlatformDetect()` 只看 `$DISPLAY` 存不存在就选 X11，**完全不看 Wayland**，
于是撞进一个不存在的 X server。

改成 `UseWaylandWithFallback()`（注意必须先 `UsePlatformDetect()` 配好回退后端，
顺序反了会抛异常）。这个坑在纯 Wayland 桌面上很常见。

---

## 功能对照

| 功能 | WPF 版 | 原生版 |
|---|---|---|
| 分享链接解析（含整段文案 + 提取码） | ✅ | ✅ |
| 文件列表勾选下载 | ✅ | ✅ |
| 多线程分片下载 | ✅ | ✅ |
| 进度 / 速度显示 | ✅ | ✅ |
| 暂停 / 继续 / 取消 | ✅ | ✅ |
| 断点续传 | ✅ | ✅ |
| 直链过期自动换链 | ✅ | ✅ |
| 登录（Cookie） | ✅ | ✅ |
| **扫码登录** | ✅ | ✅ |
| **多账号管理** | ✅ | ✅ |
| **边下边播** | ✅ | ✅ |
| **托盘常驻** | ✅ | ✅ |
| **悬浮小窗** | ✅ | ✅ |
| **浅色/深色切换** | ✅ | ✅ |
| 设置页（并发/目录/关闭行为/播放器） | ✅ | ✅ |

**功能已对齐。** 原生版在几处细节上遵循 Linux 习惯（见下）。

### 补齐的 6 项功能

**扫码登录**（`QrLoginWindow` + `QrBitmap`）
本地把 token 渲染成二维码显示，每 1.2 秒轮询，拿到 ticket 后走
`PanAccountBridge` → `OpenSslTls` → 系统 OpenSSL 换 Cookie。

> ⚠️ 这里有个坑：不能复用核心的 `QrEncoder.EncodeToBgr()` —— 它输出的是
> **GDI+ 约定**的位图（自下而上、24bpp BGR），直接喂 Avalonia 会得到
> 一张上下颠倒、扫不出来的二维码。所以 `QrBitmap` 重新用 `Encode()` 拿
> bool 矩阵，按 Skia 的 BGRA 自上而下画。

**多账号管理**（`AccountWindow` + `AccountStore`）
档案存 `~/.config/GeZi/accounts/{key}.json`，Cookie 用 AES-GCM 加密。
账号标识优先用夸克返回的稳定 uid（`QuarkClient.AccountSig`）。

**边下边播**（`PlayerLocator`）
WPF 版只认 VLC 且靠注册表探测；Linux 没有注册表，改为
「PATH → 常见安装路径」，并把范围扩到 VLC / mpv / Haruna / Celluloid。
本机已探测到 `/usr/bin/vlc`。

**托盘常驻**（`TrayHost`）
关窗时可选「收进托盘 / 直接退出 / 每次询问」，选择会被记住。
⚠️ 依赖桌面环境的 StatusNotifierItem 支持；建不起来时**自动降级**为关窗即退出，不会让程序起不来。

**悬浮小窗**（`MiniWindow`）
无边框置顶，显示总体进度。拖动用 `BeginMoveDrag()` ——
Wayland 协议**不允许客户端设置窗口位置**，手动改 `Position` 无效。

**主题切换 + 设置页**（`ThemeManager` / `SettingsWindow`）
Avalonia 的 `RequestedThemeVariant` 切换系统色板。设置页含并发数、
保存目录、主题、关闭行为、播放器路径。

> 已知取舍：主界面配色仍写死在 XAML 里（以深色为主）。
> 切到浅色时系统控件会跟着变，但窗口底/卡片色不会。
> `ThemeManager` 里已留好统一的色值入口，后续要完全跟随只需把
> XAML 里的字面量换成资源引用。

### 其他 Linux 适配

- **窗口自适应**：顶部用 `WrapPanel` 而非固定列 Grid。Hyprland/sway
  这类平铺窗口管理器会把窗口压到半屏（本机实测 630px），
  固定列布局会把右边按钮挤出可视区、点不到。
- **播放器**：VLC 传 `--started-from-file`（与 WPF 版一致），mpv 直接传路径。
- **打开目录**：`xdg-open` 代替 `explorer.exe`。

---

## 验证情况

冒烟测试 `_smoke/`，在真实机器上跑：

```
=== GeZi.Core 移植冒烟测试 (net8.0 / Linux) ===

[1] 分享链接解析
    ✅ 识别分享 ID          ✅ 整段文案取 ID
    ✅ 整段文案取提取码      ✅ URL 里的 pwd 参数
    ✅ 识别口令并标记

[2] Cookie 加密（net8 = AES-256-GCM）
    ✅ 加密后带前缀   ✅ 密文不含明文   ✅ 解密还原
    ✅ 无前缀按明文处理   ✅ 重复加密幂等   ✅ 损坏密文安全降级

[3] JSON 兼容层（net8 = System.Text.Json）
    ✅ 顶层是 Dictionary   ✅ 整数映射为 int   ✅ 数组映射为 object[]
    ✅ 大整数映射为 long   ✅ 大整数值正确     ✅ 字符串取值
    ✅ 序列化可用

[4] 多线程分片下载（真实网络）
    ✅ 下载成功   ✅ 文件已落盘且非空 [22951 字节]
    ✅ 进度有上报   ✅ 内容像样 [14494 字符]

全部通过 ✅
```

运行方式：

```bash
cd ~/githome/gezi-quark-downloader/_smoke
source ../.tools/env.sh
dotnet run -c Release
```

界面启动验证：窗口在 Hyprland 里正常显示（`class: GeZi.Avalonia`，`mapped: True`），
中文字体渲染正常。

### 应用自检（25 项）

程序内置自检，不开界面就能验证各链路：

```bash
cd ~/githome/gezi-quark-downloader/.run
./dist/GeZi.Avalonia --selftest
```

```
鸽子下载 · 原生版自检
----------------------------------------------------
▌ 运行环境
  ✓ .NET 8.0.32
  ✓ 图形后端：Wayland (wayland-1)
▌ 设置读写
  ✓ 设置往返一致
▌ 多账号
  ✓ 保存账号并读回      ✓ Cookie 加解密往返      ✓ 删除账号
▌ 二维码渲染
  ✓ 渲染位图 296×296    ✓ 编码矩阵 21×21
▌ 播放器探测（边下边播）
  ✓ 找到播放器：VLC（/usr/bin/vlc）
▌ 窗口构造（XAML 加载）
  ✓ 主窗口 ✓ 扫码登录 ✓ Cookie 登录 ✓ 账号管理
  ✓ 设置   ✓ 关闭确认 ✓ 悬浮小窗
▌ OpenSSL 通道（扫码登录前提）
  ✓ 库可加载：True  ✓ 版本：OpenSSL 3.6.5  ✓ pan.quark.cn 连通（HTTP 200）
----------------------------------------------------
通过 25 项，失败 0 项
全部通过 ✅
```

> 这个自检本身也踩过一个坑：最初用 `GetVisualDescendants()` 数控件，
> 结果 7 个窗口全报「0 个控件」。原因是**窗口未 Show 之前可视化树还没建立**。
> 改用检查 `Content` 是否为具体根控件才对。留着这个记录，免得以后重新踩。

---

## 环境依赖

| 项 | 要求 | 本机状态 |
|---|---|---|
| .NET | 8.0（自包含发布则不需要） | 8.0.426 ✅ |
| 图形 | Wayland 或 X11 | Wayland ✅ |
| OpenSSL | ≥ 3.5（扫码登录用） | 3.6.5 ✅ |
| 中文字体 | 任一 CJK | 51 个 ✅ |

`./gezi.sh --check` 会逐项检查这些。

---

## 已知限制

1. **日志里有 MESA 警告** ——
   ```
   MESA: error: ZINK: vkEnumeratePhysicalDevices failed
   ```
   这是本机 llvmpipe 软件渲染（无独立 GPU）导致的，Avalonia 会自动降级到
   软件渲染，**不影响使用**。

2. **出网代理** —— 下载器默认强制直连（那是为了夸克 CDN 的正确行为）。
   如果你的网络必须走代理，需要设 `NetworkOptions.UseSystemProxy = true`。
   开发沙箱里就遇到了这个，冒烟测试中已显式打开。

3. **浅色主题不完整** —— 窗口底色/卡片色写死在 XAML 里，切浅色时只有系统控件跟着变。
   见上文「主题切换」的说明。

4. **托盘依赖桌面环境** —— 纯 GNOME 需要装 AppIndicator 扩展。
   没有托盘时自动降级为「关窗即退出」，不影响下载。

---

## 目录说明

```
gezi-quark-downloader/
├── src/GeZi/              # WPF 版（Windows，未改动）
├── src/GeZi.Core/         # 核心（已改造成双目标）
├── src/GeZi.Avalonia/     # 原生版界面（新增）
├── _smoke/                # 冒烟测试（新增）
└── .run/
    ├── gezi.sh            # 启动脚本
    └── dist/              # 自包含发布产物
```

`.run/` 与 `_smoke/` 是本机工作产物，不涉及原仓库的二进制资产。
