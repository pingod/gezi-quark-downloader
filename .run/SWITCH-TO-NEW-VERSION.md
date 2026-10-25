# 切换到新版本

当前 `.run/dist` 是你正在运行的版本（下载已完成但进程还在）。
新版已备好在 `.run/dist-pending`。

## 切换步骤

1. **先关闭正在运行的 GeZi**（点窗口关闭按钮）
2. 执行：

```bash
cd ~/githome/gezi-quark-downloader/.run
mv dist dist-old
mv dist-pending dist
./gezi.sh
```

## 新版包含

- 17 个网盘平台（含微云 JSON RPC）
- 文件管理 UI（我的网盘 → 选平台 → 浏览/改名/移动/删除/建分享）
- 上传：夸克/UC（OSS 分片+秒传）、123（S3+秒传）、天翼（RSA+AES）
- 自动转存（7 个平台）+ 清理队列
- 代理环境变量自动检测
- 顶部两行布局（修掉了窄窗口下按钮被挤出屏幕的 bug）
- 能力声明与实际实现严格一致（122 项测试覆盖）

## 如果新版有问题，回滚

```bash
cd ~/githome/gezi-quark-downloader/.run
mv dist dist-broken
mv dist-old dist
./gezi.sh
```
