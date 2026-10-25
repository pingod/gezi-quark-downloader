# 多网盘接入清单

> 参考 wenxi（Flutter 多网盘客户端）的架构重写，**不是复制代码**。
> 规格来源：`../wenxi_research/wenxi-网盘层-C#重写技术规格.md`

---

## 总览

| 指标 | 数值 |
|---|---|
| 已接入平台 | **17 个** |
| 多网盘层代码 | 约 8,400 行 |
| 单元/集成测试 | **122 项全通过** |
| GUI 自动化测试 | **9 项全通过**（wstream 隔离沙箱） |
| 支持转存的平台 | 7 个（夸克/百度/123/天翼/迅雷/PikPak/光鸭） |
| 支持上传的平台 | **夸克/UC（OSS 分片+秒传）、123（S3+秒传）、天翼（RSA+AES 加密通道）** |

---

## 平台支持矩阵

| 平台 | 分享解析 | 个人盘 | 取链方式 | 凭据形态 | 状态 |
|---|---|---|---|---|---|
| **夸克** | ✅ 免登录 | ✅ | 游客直链 → 账号直链 → 转存 | Cookie(`__pus`+`__puus`) | **完整**（含文件管理） |
| **UC** | ✅ 免登录 | ✅ | 同夸克（复用实现） | 同夸克 | **完整** |
| **阿里云盘** | ✅ 免登录 | ✅ | 直接签名直链 | refresh_token → access_token | **完整** |
| **115** | ✅ 免登录 | ✅ | pickcode → proapi | Cookie(`UID`+`CID`+`SEID`) | ✅ 分享+个人盘 |
| **百度** | ✅ 免登录 | ✅ | **必须转存** | Cookie(`BDUSS`) | ✅ 分享+个人盘 |
| **123** | ✅ 免登录 | ✅ | **必须转存** | authorToken | ✅ 分享+个人盘 |
| **天翼** | ⚠️ 需登录 | ✅ | **分享需转存** | Cookie + browserId | ✅ 分享+个人盘 |
| **迅雷** | ⚠️ 需登录 | ✅ | **必须转存**（restore 直接返 id） | access+refresh_token | ✅ |
| **PikPak** | ⚠️ 需登录 | ✅ | **必须转存** | access+refresh+deviceId | ✅ |
| **蓝奏云** | ✅ 免登录 | ❌ | 两步 ajax 取链 | 可选 Cookie | ✅ 仅分享下载 |
| **蓝奏优享** | ❌ 平台限制 | ✅ | token 鉴权 + redirect | access+refresh | ✅ 个人盘 |
| **城通** | ✅ 免登录 | ❌ | 网页取链接口 | Session Token | ✅ 仅分享下载 |
| **光鸭云盘** | ✅ 免登录 | ✅ | 分享令牌 + 转存 | refresh_token | ✅ 含转存 |
| **小飞机** | ✅ 免登录 | ✅ | **AES-ECB 解密地址** | access+refresh+deviceId | ✅ 个人盘 |
| **联通云盘** | ✅ 免登录 | ✅ | **AES-CBC 加密通道** | refresh_token | ✅ 个人盘 |
| **腾讯微云** | ❌ 平台限制 | ✅ | **JSON RPC 套壳协议** | Cookie(多种登录态) | ✅ 个人盘 |
| **GitHub** | ✅ 免登录 | ✅ | 直接拼 raw 地址 | PAT（可选） | ✅ 只读浏览 |

图例：✅ 已实现　⚠️ 需登录才能用　❌ 不适用

---

## 三个核心设计

### 1. `DownloadSpec` —— 取链的统一描述

```csharp
Url                  // 直链（空 = 还需要二次取链）
NeedsPreparation     // = url空 && source非空
Source               // 二次取链所需的上下文（fid/分享凭证/平台）
Cleanup              // 下载完要删的临时转存目录
Headers              // 下载时必须带的请求头
```

`NeedsPreparation` 用一个字段区分了两族网盘：
- **夸克/UC/阿里/115/蓝奏/城通/GitHub** → 解析即给直链
- **百度/123/天翼/迅雷/PikPak** → 必须先转存到自己账号

这个区分至关重要：不区分就会把"能下"的场景做成"失败"。

### 2. `Headers` —— 平台差异的隔离点

各平台直链的请求头要求差异极大，**不能统一处理**：

| 平台 | 必需头 | 特殊要求 |
|---|---|---|
| 百度 | Cookie(含BDCLND) + UA + Referer | 必须 `Accept-Encoding: identity`、`Content-Transfer-Encoding: binary`，缺则 403 |
| 阿里 | UA + Referer | OSS 签名里绑了 Referer |
| 115 | UA + Referer | **不能带登录 Cookie**（带了反判盗链） |
| 微云 | 临时 cookie | 从下载响应里提取 |
| 夸克/UC | Cookie + UA + Referer | 客户端 UA 伪装 |

实现上，provider 通过 `spec.Headers` 提供，下载器**最后一步合并**（外部优先）。
空值表示"显式移除该头"——这是 115 那种"不能带 Cookie"场景的关键。

### 3. 令牌刷新 —— 单飞 + 双重检查

OAuth 类平台（阿里/PikPak/迅雷/城通…）的 refresh_token 多数是**一次性**的。
并发刷新会让先刷的那个立刻失效。解决方式：

```
① 进锁前检查（快路径，绝大多数请求不走锁）
② 拿到锁后再检查（别人可能刚刷完）
③ 真刷新前再确认（access 变了就直接用新值，不重复刷）
```

`TokenCloudProviderBase` 实现了这套逻辑，9 个平台共用。

---

## 实现中踩到的坑

### 坑 1：net48 不支持默认接口方法

最初用「接口 + 默认接口实现（DIM）」组织代码，net48 直接报 `CS8701`。
因为本项目要同时编 net48（Windows WPF）和 net8.0。

改成经典的「`ICloudProvider` 接口 + `CloudProviderBase` 抽象基类」才两边都能编。
**接口只声明必须实现的方法，基类提供"不支持就抛统一异常"的默认实现。**

### 坑 2：夸克取链的错误码不能一律当失败

这是最严重的一处。原实现把所有非成功码都抛成"取链失败"，但正确行为是分流的：

| 错误码 | 正确行为 | 错误做法 |
|---|---|---|
| `14001`/`41020` | 凭证过期 → **刷新重试** | 报失败 |
| `23018`/`31001` | 需登录/受限 → **降级到转存** | 报失败 |
| `401`/`403` | 无权限 → 降级转存 | 报失败 |
| `404`/`405` | 接口不存在 → 可重试 | 报失败 |

不做分流，等于**把一个本来能成功的场景做成失败**——文件明明能通过转存下载。

已修复：新增 4 个常量 + `QuarkShareCredentialExpiredException`（可重试）
+ `CloudNewPreparationException`（改走转存）。UI 会显示「需转存」/「需重试」而非红叉。

### 坑 3：对"应该照抄"的建议做了核实后保留意见

规格文档建议照抄 `quark_route_1` = **64 KiB 分段预算**。我算了下差异：

```
   40 MB | 当前: 160 片 × 256 KB | 建议: 512 连接 × 80 KB
 1000 MB | 当前: 2048 片 × 500 KB | 建议: 512 连接 × 2000 KB
```

**没有改**，因为代码里有明确警告：

> 🚨 我们当初抄云析时只抄了 `×8`，漏了后两层夹取；后来又自作主张把 `×8` 改成 `×32`
> —— 双重放大，结果 9.5GB 文件切出 16384 片。**现在完整对齐云析**：
> `min(max(bySize, threads*8), total/256KB, 512)`。实测 9.5GB + 256 线程 → 512 片 × 18.6MB，
> 正好回到「与 Python 持平（1.01x）」的配置。

这是项目之前**踩过坑并调优过**的成果。是否改必须用真实账号实测速度决定，
不能凭"另一个项目这么做"就改。

---

## 转存与清理（已完成）

### 转存流程（`CloudTransferFlow`）

6 个平台需要"先转存再取链"，统一走 5 步模板：

```
1. 建唯一临时目录        AsterLink临时转存_<uuid>
2. 立刻登记 cleanup      ← 必须在转存之前
3. 执行转存
4. 轮询等可见            8~30 次 × 间隔
5. 用新 fileId 取链
```

**第 2 步为什么必须在第 3 步之前**：如果先转存再登记，转存成功后程序崩溃，
临时目录就永远留在用户网盘里了。先登记，清理队列才知道有东西要删。

各平台差异：

| 平台 | 临时目录 | 轮询 | 可见性判定 |
|---|---|---|---|
| 123 | 单级 uuid | 8 × 1s | 名字+大小，且唯一 |
| 百度 | 两级 `文析助手临时转存/tr_xxx` | 不轮询（直接返 `to_fs_id`） | 用 `/api/filemetas` 确认 |
| 天翼 | 单级 uuid | 8 × 0.8s | 名字+大小 |
| 迅雷 / PikPak | 单级 uuid | **不轮询**（restore 直接返 id 列表） | 直接用返回的 id |

### 清理队列（`CleanupQueue`）

持久化到 `~/.config/GeZi/cleanup-queue.json`，采用**租约模型**：

```
Retain  → 下载开始 +1
Release → 下载结束 -1
Drain   → 只清理引用数为 0 的条目
```

**为什么不能"下载完就删"**：
1. 一个临时目录可能被多个任务共享，早完成的任务先删会导致其他任务取链失败
2. 程序中途退出时，临时目录会留在网盘里 —— 必须持久化，下次启动接着清
3. 清理本身可能失败（网络/限流/token 过期），要能重试

**安全边界**：清理前必须校验目录名匹配 `AsterLink临时转存_<uuid>`。
队列文件是持久化的，如果被篡改，没有这层校验就可能删掉用户真实目录。
测试里专门验证了这条（塞一个非法名字的条目，确认删除函数不会被调用）。

---

## 文件管理 UI（已完成）

| 操作 | 入口 | 支持平台 |
|---|---|---|
| 浏览个人网盘 | 顶部「我的网盘」 | 夸克（其余平台 provider 就绪） |
| 新建文件夹 | 文件列表工具条 | 夸克/UC |
| 重命名 | 同上（需先选中） | 夸克/UC |
| 移动 | 同上（需先选中） | 夸克/UC |
| 删除 | 同上（需先选中） | 夸克/UC、115 |
| 创建分享 | 同上（需先选中） | 夸克/UC |

按钮按三种条件自动显隐：
1. 是否在**个人盘模式**（分享浏览模式下不能改文件）
2. 该平台是否支持这个操作（`SupportsRename` 等）
3. 是否已选中条目（重命名/移动/删除/分享需要）

创建分享后，链接和提取码会自动复制到剪贴板并展示在对话框里。

---

## 上传（三个平台）

### 夸克 / UC —— OSS 分片 + 秒传

夸克/UC 走标准 OSS 分片上传，完整流程：

```
① POST file/upload/pre    → task_id / bucket / obj_key / upload_id
② POST file/update/hash   → 传 md5+sha1，若 finish=true 则【秒传成功】
③ POST file/upload/auth   → 用 auth_meta 换 OSS 签名
④ PUT  到 OSS             → 分片上传（8MB/片）
⑤ POST file/upload/finish → 提交拿 fid
```

**秒传是关键优化**：服务端已有相同文件（md5+sha1 命中）时，
第 ② 步就返回 `finish=true`，整个上传瞬间完成，一个字节都不用传。

### 123 网盘 —— S3 直传 + 秒传

```
① POST upload_request → 若 Reuse=true 则【秒传成功】，不传字节
② 拿 S3 凭据（AccessKeyId/SecretAccessKey/EndPoint）
   或走预签名 URL：s3_upload_object/auth（单片）/ s3_repare_upload_parts_batch（多片）
③ PUT 到 S3（16MB/片）
④ POST upload_complete/v2 提交
```

### 天翼云盘 —— RSA + AES 双层加密通道

天翼上传走**完全独立的加密通道**，是三个里最复杂的：

```
① 取 sessionKey
② GET /security/generateRsaKey → 服务端 RSA 公钥 + pkId
③ 每次调用都要三件套：
   随机 24 位 secret
   → AES-128-ECB(secret[0:16]) 加密 params
   → HMAC-SHA1(secret) 签名
   → RSA(公钥) 加密 secret 放进 EncryptionText 头
④ initMultiUpload（fileDataExists=1 → 秒传）
   → getMultiUploadUrls（每片一个预签名 URL + 自定义头）
   → PUT 每片（10MB/片）→ commitMultiUploadFile
```

⚠️ net48 没有 `RSA.ImportSubjectPublicKeyInfo`，所以手写了
最小 ASN.1 解析器从 SPKI 提取 n/e（避免为解一个公钥引入 BouncyCastle）。

**验证**：测试里用真实 RSA 密钥对做了往返——加密后用私钥解回 secret，
再用 secret 解 AES，能还原原始参数串。这条链完全正确。

其余平台的上传协议各异（百度 precreate+superfile2、小飞机/光鸭各有加密流程），
接口已就绪（`UploadAsync`），实现待补。

---

## 界面入口

### 我的网盘（平台选择器）

顶部「我的网盘」→ 弹出平台列表 → 选一个打开。列表里显示每个平台的
能力标签（分享免登录/需登录 · 自动转存 · 上传 · 建分享）。

### 文件管理

| 操作 | 已实现平台 |
|---|---|
| 浏览个人网盘 | 夸克/UC/阿里/115/百度/123/天翼/迅雷/PikPak/光鸭/小飞机/优享/城通/联通/微云/GitHub |
| 新建文件夹 | 夸克/UC |
| 重命名 | 夸克/UC |
| 移动 | 夸克/UC |
| 删除 | 夸克/UC/115 |
| 创建分享 | 夸克/UC |
| **上传文件** | **夸克/UC（OSS）、123（S3）、天翼（RSA+AES）** |

按钮按三个条件自动显隐：是否个人盘模式、平台是否支持该操作、是否已选中条目。

### 能力声明必须等于实际实现

这里踩过一个真实的设计错误，值得记下来：

基类原本把 `SupportsRename` / `SupportsMove` / `SupportsDelete` /
`SupportsCreateFolder` 默认设成 **true**（"大多数平台都支持"的直觉），
`SupportsCreateShare` 则按 `Platform.SupportsSharing` 判断。

结果：**17 个平台全都显示这些按钮，除夸克外一点就抛"暂不支持"**。

改成默认 **false**，由真正实现了对应方法的 provider 显式覆盖为 true。
并且加了一条专项测试 `TestCapabilityHonesty`，列出每个平台的期望能力，
逐项核对声明与实际 —— 这个测试当场抓出了 10 处不符。

---

## GUI 自动化测试

`./.run/gui-test.py` —— 在 wstream 隔离沙箱里跑，不碰用户桌面。

```
✓ 应用启动并出窗口
✓ 启动日志正常
✓ 代理自动检测生效
✓ 『我的网盘』弹出平台选择器
✓ 平台选择器截图
✓ 选择器可关闭
✓ 『设置』窗口可打开
✓ 运行期间无未处理异常
✓ 应用仍在运行
通过 9 项，失败 0 项
```

### 三个 GUI 测试的坑（都实际踩过、都已修）

**坑 1：坐标必须按实际屏幕尺寸换算**

`WStream()` 默认 `width=1280, height=720`，而沙箱是 1600×1000。
不传的话所有坐标偏移约 25%，表现为「点了按钮完全没反应」，
极难排查（我查了 5 轮才定位到）。

**坑 2：沙箱进程随调用结束被回收**

启动沙箱 → 启动应用 → 操作 → 截图，必须**在同一次进程**里完成。
分多次跑的话第二次连 WebSocket 都连不上（`Connection refused`）。

**坑 3：窄窗口下按钮会溢出屏幕**

原本顶部是 `StackPanel` 横向排三个按钮，窗口被平铺成 534px 时
「账号」「设置」被挤出屏幕右边缘（只剩半个「账」字），点不到。

改成 WrapPanel 无效 —— 外层是 Grid 的 Auto 行，WrapPanel 拿不到
"可用宽度"的约束，实际不会换行。最终改成**显式的两行布局**
（第一行标题+账号，第二行按钮），彻底解决。

---

## 微云协议（JSON RPC 套壳）

微云**不是**二进制协议，而是把 JSON 塞进两层字符串里：

```
POST https://www.weiyun.com/webapp/json/{protocol}/{name}?g_tk={csrf}&cmd={cmd}
{
  "req_header": "{seq, cmd, qq_openid, user_flag, appid:30013, version:3...}",
  "req_body":   "{\"ReqMsg_body\":{\"ext_req_head\":{token_info},
                  \".weiyun.{name}MsgReq_body\":{实际参数}}}"
}
```

三个必须注意的点：

1. **协议名与 cmd 号必须配对**
   - `DiskDirList` (2208, `weiyunQdisk`)
   - `DiskFileBatchDownload` (2402, `weiyunQdisk`)
   - `LibDirPathGet` (26150, `weiyunFileLibClient`)

   配对错了服务端返回**空对象而不是报错**，极难排查。

2. **token_info 要从 Cookie 推导**
   - 有 `weiyun_qq_openid` / `weiyun_wx_openid` → token_type=2
   - 有 `openid` + `access_token` → token_type=1
   - 否则用 `wy_uf` / `p_skey` → token_type=0

3. **下载要带临时 Cookie**：接口返回 `cookie_name`/`cookie_value`，
   下载时必须写成 `Cookie: name=value`。这两个值做了**格式校验**
   （名字只允许字母数字下划线点横线、值不能含分号/CR/LF），防头注入。

---

## 未完成部分

### 1. 微云上传未实现

浏览/取链/重命名/移动/删除都已实现，但上传另有一套
`weiyun_hash` 校验协议（预计算文件分片哈希），暂未接入。

### 2. 其余平台的上传未实现

已实现：夸克/UC（OSS）、123（S3）、天翼（RSA+AES）。
未实现：百度、阿里、115、迅雷、PikPak、光鸭、小飞机、蓝奏、城通、联通、微云。
这些平台的上传协议各异，接口已就绪（`UploadAsync`）。

### 3. Android 端

用户明确要求放最后。`ICloudProvider` 抽象层是纯 .NET，可复用；
UI 层需重写。

