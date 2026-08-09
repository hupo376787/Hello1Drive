# Microsoft Entra / OneDrive 配置

Hello1Drive 使用同一个 Microsoft Entra 应用注册，Client ID：

`9ea6a8b7-0122-4c9a-8b14-752d60de9626`

## 1. API 权限

Microsoft Graph → **委托的权限**：

- `User.Read`
- `Files.ReadWrite`

不需要 `Files.ReadWrite.All`。

## 2. 公共客户端

Authentication → Settings → **允许公共客户端流 = 是**。

## 3. Desktop：Windows / Linux / macOS

在“移动和桌面应用程序”中添加：

`http://localhost`

Desktop 使用 MSAL.NET + 系统浏览器 + loopback 回调。

## 4. Android

应用包名：

`com.xiaowei.hello1drive`

添加移动/桌面自定义重定向 URI：

`msal9ea6a8b7-0122-4c9a-8b14-752d60de9626://auth`

项目中 `MsalActivity.cs` 已注册对应 scheme。

## 5. iOS

Bundle ID：

`com.xiaowei.hello1drive`

在 Entra 中添加 **iOS / macOS** 平台，Bundle ID 填上面的值，对应 URI：

`msauth.com.xiaowei.hello1drive://auth`

项目 `Info.plist` 已注册 `msauth.com.xiaowei.hello1drive` URL scheme；`Entitlements.plist` 已配置 `com.microsoft.adalcache` Keychain group。Avalonia 12 使用 scene lifecycle，因此回调由 Core 中的 `IActivatableLifetime` 转发给 iOS MSAL 服务。

## 6. Browser / WASM

Browser 是 SPA，使用 Authorization Code + PKCE。必须单独添加 **单页应用程序 (SPA)** 重定向 URI。

本地开发固定为：

`http://localhost:5173/`

部署后还要把线上完整 URL 加进去，例如：

`https://example.com/hello1drive/`

浏览器回调地址由 `window.location.origin + window.location.pathname` 自动计算，所以 Entra 中要与实际访问地址完全匹配。

> Browser 的 Token 存在浏览器 localStorage。不要在站点中加载不可信脚本；生产环境必须使用 HTTPS。
