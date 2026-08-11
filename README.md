# Hello1Drive

一个基于 **Avalonia 12 + .NET 10 + Microsoft Graph** 的全平台 OneDrive 客户端。

整体结构沿用 HelloCrab 的分层方式：共享 `Hello1Drive.Core` 承载 UI、MVVM、Graph 业务逻辑，再由 Desktop / Android / iOS / Browser 平台头项目提供认证与平台入口。Desktop 同一个项目发布 Windows、Linux、macOS 六个 RID。

程序主色为 `#FD6F71`，应用图标使用项目 `Assets/app-icon.*` 中的珊瑚色云端图标；Android Application ID 为 `com.xiaowei.hello1drive`。

## 平台

| 平台 | 项目 | 目标 |
|---|---|---|
| Windows x64 / arm64 | `Hello1Drive.Desktop` | `net10.0` |
| Linux x64 / arm64 | `Hello1Drive.Desktop` | `net10.0` |
| macOS x64 / arm64 | `Hello1Drive.Desktop` | `net10.0` |
| Android | `Hello1Drive.Android` | `net10.0-android36.0` |
| iOS / iPadOS | `Hello1Drive.iOS` | `net10.0-ios26.0` |
| Browser / WASM | `Hello1Drive.Browser` | `net10.0-browser` |

## 已实现功能

### 文件管理

- Microsoft 个人账户登录 / 退出，登录后显示 Microsoft 头像。
- OneDrive 根目录、子目录浏览、面包屑导航、返回、根目录、刷新；目录采用分页/滚动增量加载，首屏先取一页，接近底部自动加载下一页。最近访问目录使用内存缓存，返回父目录时恢复原滚动位置，进入子目录从顶部开始；可选择记住退出时所在目录，下次登录后直接恢复。
- 当前目录搜索。
- 新建文件夹、重命名、删除到 OneDrive 回收站。
- 可显示/隐藏的悬浮上传按钮，支持自由拖拽并记忆位置；文件选择器支持 **多选上传**。
- 文件列表支持 `Ctrl` / `Shift` 多选和空白区域鼠标框选；下载按钮支持文件及文件夹，选择文件夹时会递归下载其全部子文件夹和文件。
- 上传 / 下载任务面板：批量选择后所有任务立即入列，显示待传输 / 正在传输 / 已完成 / 错误、实时进度；失败任务可一键重试；入口位于 Desktop 标题栏设置按钮左侧。
- 大文件使用 Microsoft Graph Upload Session，默认 10 MiB 分片。

### 文件展示与预览

- 三种视图：**详细信息 / 大图标 / 超大图标**。
- 详细信息列支持名称、大小、修改时间排序；设置中可指定全局默认排序（系统默认/日期/名称/大小升降序），每次修改全局默认都会清空并覆盖全部文件夹旧的独立排序。之后每个文件夹仍可单独覆盖，也可选择“跟随设置默认”或仅该目录使用“系统默认”。类型列仅显示，不提供排序。大小排序会为兼容部分 OneDrive 后端自动附加非索引查询 Prefer；若后端仍返回 `SMTotalFileStreamSize` 501，则仅该目录回退系统默认顺序。
- 每一列按 **升序 → 降序 → 原有顺序** 三态循环。
- 双击文件打开蒙层预览；点击预览卡片外部关闭。Desktop 右键、移动端长按文件可弹出打开/下载/重命名/删除/网页打开等常用操作菜单。
- 文本文件使用 Hello1Drive 内置编辑器打开，并可直接保存回 OneDrive。
- 图片 / 动态 GIF 在应用内蒙层预览，默认自动适应预览窗体；无滚动条，鼠标滚轮始终以指针所在位置为中心缩放，范围 1%–800%，缩放比例显示在预览区顶部中央；鼠标/触摸可拖拽平移。手机端“更多”和图片静止长按共用居中深色操作面板，系统返回优先关闭操作面板；滑动翻页、双指缩放和放大后的拖动不会误触长按菜单。
- 预览支持上一项/下一项按钮与键盘方向键/PageUp/PageDown；图片右键提供上一张、下一张、幻灯片、下载、详细信息，幻灯片间隔可在设置中调整。预览下载/解码过程中可直接关闭或在移动端按返回键取消。
- 已打开文件使用本地持久缓存；再次打开时优先使用目录列表携带的版本标识校验，文件未变化则直接使用缓存，变化后才重新下载。图片/视频缩略图也使用独立的持久磁盘缓存：Desktop 位于程序目录 `cache/thumbnails`，移动端位于应用数据目录；重新启动后只要文件版本未变化，就直接从本地缩略图缓存显示，不再重新下载缩略图。设置中的“清除缓存”会同时清除原文件缓存和缩略图缓存。
- 其它文件至少进入统一的蒙层信息预览，并提供“使用系统应用打开”。设置中“使用内置查看器”默认开启；关闭后，打开任意文件都会先写入持久缓存，再交给操作系统默认应用。系统无法处理该类型时保留预览页并显示“暂不支持”。Desktop LibVLC 视频自然播放结束后再次点击播放会从头重新播放。
- Desktop 已内置基于 `LibVLCSharp.Avalonia` 的视频/音频播放器，直接播放 Hello1Drive 本地文件缓存；Android 使用原生 `VideoView`。视频控制栏统一把单个播放/暂停状态按钮放在进度条前面，Android 不再使用会自动带前进/后退键的系统 `MediaController`。不支持内置播放的平台/格式可使用系统默认应用打开，详见 `docs/MEDIA_PREVIEW.md`。

### 界面

- 全局主题色：`#FD6F71`。
- 登录按钮使用 `#FD6F71`，文字水平 / 垂直居中。
- Desktop 移除系统标题栏，第一行文件工具栏直接并入自绘标题栏；支持拖动、双击最大化、最小化、最大化/还原、关闭。
- 标题栏与底部状态栏均为透明背景，让自定义壁纸完整贯穿窗口；两行工具区采用紧凑高度。
- 标题栏设置按钮打开右侧滑入的 HelloV 风格亚克力设置面板（当前壁纸副本 + 模糊 + 半透明色层）。
- 用户头像菜单：**进入网页版 / 设置 / 退出登录**。
- 文件区空白处右键/长按提供上传、新建文件夹、排序方式和视图方式，行为接近资源管理器。
- Desktop 关闭窗口前使用 HelloCrab 风格确认蒙层。
- 退出登录使用 HelloCrab 风格确认蒙层。
- 新建 / 重命名 / 删除等对话框统一使用相同的蒙层卡片风格。
- Desktop 的可点击路径层级直接位于标题栏；路径按钮无边框、透明背景。搜索框同样无边框。

### 主题与窗体背景

设置面板支持：

- 跟随系统 / 浅色 / 深色主题。
- 默认背景。
- 自定义纯色。
- 本地单张图片。
- 图片 URL。
- 本地图片文件夹。
- OneDrive 图片文件夹（先进入目标目录，再选择“使用当前 OneDrive 文件夹”）。
- 文件夹背景支持设置图片轮换时间，单位为分钟。
- 本地图片 / 文件夹使用 Avalonia StorageProvider Bookmark 持久化授权，不依赖直接文件路径。
- 可记住当前目录、显示/隐藏并记忆悬浮上传按钮位置、切换文件项背景是否透明，并提供清除缓存按钮；右键“缓存”文件/文件夹时，会同时预热图片/视频缩略图缓存，文件夹会递归处理子目录。
- 可设置“删除前确认”、“使用内置查看器”开关，以及幻灯片切换时间。
- 可分别限制上传/下载速度（KB/s）。
- 提供“下载所有 OneDrive 文件”，执行前会提示可能消耗大量流量与磁盘空间；确认后递归创建目录并加入传输列表。

## 目录

```text
Hello1Drive/
├─ src/
│  ├─ Hello1Drive.Core/       # UI、ViewModel、Graph API、平台抽象、设置与传输模型
│  ├─ Hello1Drive.Desktop/    # Windows / Linux / macOS + MSAL
│  ├─ Hello1Drive.Android/    # Android + MSAL callback
│  ├─ Hello1Drive.iOS/        # iOS + MSAL callback
│  └─ Hello1Drive.Browser/    # Avalonia WASM + Browser PKCE
├─ scripts/
│  ├─ one-click-publish.ps1
│  └─ one-click-publish.sh
├─ .github/workflows/
│  ├─ build.yml
│  └─ release.yml
├─ docs/
│  ├─ ENTRA_SETUP.md
│  └─ MEDIA_PREVIEW.md
└─ one-click-publish.cmd
```

## 环境

建议安装 .NET 10 SDK：

```bash
dotnet --info
```

按目标平台安装 workload：

```bash
dotnet workload install android ios wasm-tools
```

iOS 需要在 macOS + Xcode 环境构建。

## Entra 配置

先阅读 [`docs/ENTRA_SETUP.md`](docs/ENTRA_SETUP.md)。

当前代码使用 Client ID：

```text
9ea6a8b7-0122-4c9a-8b14-752d60de9626
```

Graph Delegated Permissions：

```text
User.Read
Files.ReadWrite
```

Desktop redirect URI：

```text
http://localhost
```

Browser 本地开发 SPA redirect：

```text
http://localhost:5173/
```

## 本地运行

### Desktop

```bash
dotnet run --project src/Hello1Drive.Desktop/Hello1Drive.Desktop.csproj
```

### Android

```bash
dotnet build src/Hello1Drive.Android/Hello1Drive.Android.csproj -t:Run -f net10.0-android36.0
```

### Browser

```bash
dotnet workload install wasm-tools
dotnet run --project src/Hello1Drive.Browser/Hello1Drive.Browser.csproj
```

默认本地地址：`http://localhost:5173/`。

### iOS Simulator

```bash
dotnet build src/Hello1Drive.iOS/Hello1Drive.iOS.csproj -f net10.0-ios26.0 -r iossimulator-arm64 -t:Run
```

## 一键发布

### Windows：只运行根目录 CMD

Windows 用户推荐直接双击项目根目录：

```bat
one-click-publish.cmd
```

它是 Windows 的统一入口，会自动调用 `scripts\one-click-publish.ps1`。**不需要先运行 CMD，再单独运行 PS1。**

不带参数运行时会显示发布目标菜单。普通 Windows x64 电脑选择 `Windows x64` 即可。也可以直接从命令行指定：

```bat
one-click-publish.cmd win-x64
one-click-publish.cmd windows
one-click-publish.cmd desktop
one-click-publish.cmd android
one-click-publish.cmd browser
one-click-publish.cmd all
```

其中：

- `win-x64`：只发布 Windows x64，日常最推荐，速度最快。
- `windows`：发布 Windows x64 + Windows ARM64。
- `desktop`：发布 Windows / Linux / macOS 六个 Desktop RID。
- `android`：只发布 Android。
- `browser`：只发布 Browser。
- `all`：发布全部可在当前机器完成的目标；Windows 上 iOS 会自动跳过。

`scripts/one-click-publish.ps1` 是 CMD 调用的实际 PowerShell 实现。高级用户也可以直接执行，但普通 Windows 用户不需要直接运行它。该脚本现在保持 ASCII 内容，以兼容 Windows PowerShell 5.1；CMD 会优先使用 PowerShell 7 (`pwsh`)，不存在时再回退到 Windows PowerShell。

### macOS / Linux

使用：

```bash
chmod +x ./scripts/one-click-publish.sh
./scripts/one-click-publish.sh all
```

输出目录：

```text
publish/
artifacts/
```

Desktop RID：

```text
win-x64
win-arm64
linux-x64
linux-arm64
osx-x64
osx-arm64
```

## GitHub Actions

`build.yml`：push / PR 自动构建 Desktop 六 RID、Browser、Android、iOS Simulator。

`release.yml`：推送 `v*` Tag 后构建发布包并创建 GitHub Release：

```bash
git tag v1.0.0
git push origin v1.0.0
```

Android workflow 生成 APK/AAB 构建产物；正式商店发布时需要配置自己的 keystore。

iOS workflow 默认生成 Simulator arm64；真机 / App Store IPA 需要 Apple Developer 证书和 Provisioning Profile。

## 认证实现

Desktop：

```text
MSAL.NET → 系统浏览器 → http://localhost → token cache → Microsoft Graph
```

Android：

```text
MSAL.NET → system browser/custom tab → msal{ClientId}://auth
```

iOS：

```text
MSAL.NET → system web authentication → msauth.com.xiaowei.hello1drive://auth
```

Browser：

```text
Authorization Code + PKCE → SPA redirect → token endpoint → Microsoft Graph
```

## 注意

1. Desktop 的 MSAL cache 保存在当前用户 LocalApplicationData 下；如需进一步强化，可替换为系统 Keychain / Secret Service。
2. Browser token 放在 `localStorage`，生产部署请使用 HTTPS，并避免加载不可信第三方脚本。
3. iOS 的 MSAL 自定义 URI 回调通过 Avalonia `IActivatableLifetime` 转发给 iOS MSAL 服务。
4. `Files.ReadWrite` 足够完成当前用户 OneDrive 中的创建、上传、下载、重命名和删除，无需把权限扩大到 `Files.ReadWrite.All`。


### Browser / WASM 登录回调

开发环境使用固定 SPA 重定向 URI：`http://localhost:5173/browser-auth`。请在 Microsoft Entra 的“单页应用程序”平台中注册该地址。桌面端仍使用 `http://localhost`。

## 目录导航与视图记忆

- OneDrive 目录加载支持可取消导航：子目录还在加载时按系统返回会立即取消当前请求并恢复父目录，不再等待请求完成。
- 每个 Microsoft 账户下，每个 OneDrive 目录都会独立记住“详细信息 / 大图标 / 超大图标”视图；根目录同样独立记忆。
- 尚未访问过的目录使用现有全局 ViewMode 作为首次默认值。

### Android 滚动与预览过渡（2026-08）

- 所有文件从列表进入预览页时增加轻量淡入过渡，图片、文本、视频和通用文件预览共用。
- 手机缩略图改为 360 项有界解码 LRU：滚回刚看过的项目直接复用 Bitmap，不再主动清空为占位图。
- 已存在磁盘缓存的缩略图允许在 fling 期间后台解码；未缓存的网络缩略图仍在靠近可视区域/滚动结束后加载。
- 缩略图解码移出 UI Dispatcher，最终属性赋值才回到 UI 线程。
- 手机滚动期间不再立即折叠/展开顶部和底部栏，避免把 Auto 行重排插进滚动帧；滚动静止后再提交显隐。
- 更详细的分析见 `docs/ANDROID_SCROLL_PERFORMANCE.md`。

- 移动/复制目标目录支持逐层浏览 OneDrive 任意层级文件夹；切换当前文件夹时自动清空当前文件夹搜索关键词。

### 启动与加载体验

- 启动增加 Hello1Drive 闪屏；缓存目录仍在后台立即恢复，闪屏不会等待 Graph 同步完成。
- 所有不确定时长的加载状态统一为“环形进度 + 加载中”，加载层背景透明。
- 文件夹进入只把首个 children 请求放在关键路径；childCount 缺失时后台补齐，缓存目录无 Loading 闪烁。
- 手机根目录按系统返回会弹出关闭确认对话框；设置中的“下载所有 OneDrive 文件”也改为居中的确认对话框。

- Windows 桌面端支持“开机启动”：登录 Windows 后使用 `--tray` 参数自动在系统托盘运行。

### Android 后台传输

Android 上传、下载和缓存任务在存在等待/运行中的传输时会启动 `dataSync` 前台服务，切换到其他 App 后继续执行，并通过系统通知显示当前任务数量。任务全部结束后服务会自动停止。Android 15+ 对 `dataSync` 前台服务有系统级运行时长限制，详见 `docs/ANDROID_BACKGROUND_TRANSFERS.md`。
