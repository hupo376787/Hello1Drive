param(
    [ValidateSet("all", "desktop", "browser", "android", "ios")]
    [string]$Target = "all",
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Publish = Join-Path $Root "publish"
$Artifacts = Join-Path $Root "artifacts"

New-Item -ItemType Directory -Force -Path $Publish, $Artifacts | Out-Null

function Invoke-DotNetPublish([string]$Project, [string[]]$Args) {
    Write-Host "dotnet publish $Project $($Args -join ' ')" -ForegroundColor Cyan
    & dotnet publish $Project @Args
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $Project" }
}

function New-Zip([string]$Source, [string]$Zip) {
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    Compress-Archive -Path (Join-Path $Source "*") -DestinationPath $Zip -CompressionLevel Optimal
    Write-Host "Artifact: $Zip" -ForegroundColor Green
}

function Publish-Desktop {
    $project = Join-Path $Root "src/Hello1Drive.Desktop/Hello1Drive.Desktop.csproj"
    $rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")

    foreach ($rid in $rids) {
        Write-Host "`n=== Desktop: $rid ===" -ForegroundColor Yellow
        $targetDir = Join-Path $Publish "desktop/$rid"
        $rawDir = if ($rid.StartsWith("osx-")) { Join-Path $targetDir "raw" } else { $targetDir }
        Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $rawDir | Out-Null

        Invoke-DotNetPublish $project @(
            "-c", $Configuration,
            "-r", $rid,
            "--self-contained", "true",
            "-o", $rawDir,
            "/p:Version=$Version",
            "/p:PublishSingleFile=false"
        )

        if ($rid.StartsWith("osx-")) {
            $app = Join-Path $targetDir "Hello1Drive.app"
            $macos = Join-Path $app "Contents/MacOS"
            $resources = Join-Path $app "Contents/Resources"
            New-Item -ItemType Directory -Force -Path $macos, $resources | Out-Null
            Copy-Item (Join-Path $rawDir "*") $macos -Recurse -Force
            Copy-Item (Join-Path $Root "src/Hello1Drive.Core/Assets/app-icon.icns") (Join-Path $resources "app-icon.icns") -Force
            $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>Hello1Drive</string>
<key>CFBundleDisplayName</key><string>Hello1Drive</string>
<key>CFBundleIdentifier</key><string>com.xiaowei.hello1drive</string>
<key>CFBundleExecutable</key><string>Hello1Drive</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>app-icon.icns</string>
<key>CFBundleShortVersionString</key><string>$Version</string>
<key>CFBundleVersion</key><string>$Version</string>
</dict></plist>
"@
            Set-Content -Path (Join-Path $app "Contents/Info.plist") -Value $plist -Encoding UTF8
            Remove-Item $rawDir -Recurse -Force
            if (-not $IsMacOS) {
                Write-Warning "从 Windows 复制 .app 到 macOS 后执行：chmod +x Hello1Drive.app/Contents/MacOS/Hello1Drive；正式发布建议在 macOS/CI runner 打包。"
            }
        }

        New-Zip $targetDir (Join-Path $Artifacts "Hello1Drive-Desktop-$rid-$Version.zip")
    }
}

function Publish-Browser {
    Write-Host "`n=== Browser ===" -ForegroundColor Yellow
    $project = Join-Path $Root "src/Hello1Drive.Browser/Hello1Drive.Browser.csproj"
    & dotnet workload install wasm-tools
    if ($LASTEXITCODE -ne 0) { throw "wasm-tools workload install failed" }
    Invoke-DotNetPublish $project @("-c", $Configuration, "/p:Version=$Version")

    $source = Join-Path $Root "src/Hello1Drive.Browser/bin/$Configuration/net10.0-browser/publish/wwwroot"
    $targetDir = Join-Path $Publish "browser"
    Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item (Join-Path $source "*") $targetDir -Recurse -Force
    New-Zip $targetDir (Join-Path $Artifacts "Hello1Drive-Browser-$Version.zip")
}

function Publish-Android {
    Write-Host "`n=== Android ===" -ForegroundColor Yellow
    $project = Join-Path $Root "src/Hello1Drive.Android/Hello1Drive.Android.csproj"
    & dotnet workload install android
    if ($LASTEXITCODE -ne 0) { throw "android workload install failed" }
    Invoke-DotNetPublish $project @("-c", $Configuration, "-f", "net10.0-android36.0", "/p:Version=$Version")

    $targetDir = Join-Path $Publish "android"
    Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Get-ChildItem (Join-Path $Root "src/Hello1Drive.Android/bin/$Configuration/net10.0-android36.0/publish") -File |
        Where-Object { $_.Extension -in ".apk", ".aab" } |
        Copy-Item -Destination $targetDir
    New-Zip $targetDir (Join-Path $Artifacts "Hello1Drive-Android-$Version.zip")
}

function Publish-iOS {
    Write-Host "`n=== iOS simulator ===" -ForegroundColor Yellow
    if (-not $IsMacOS) {
        Write-Warning "iOS 需要 macOS + Xcode。当前系统不是 macOS，已跳过 iOS。"
        return
    }
    $project = Join-Path $Root "src/Hello1Drive.iOS/Hello1Drive.iOS.csproj"
    & dotnet workload install ios
    if ($LASTEXITCODE -ne 0) { throw "ios workload install failed" }
    Invoke-DotNetPublish $project @(
        "-c", $Configuration,
        "-f", "net10.0-ios26.0",
        "-r", "iossimulator-arm64",
        "/p:CodesignKey=",
        "/p:CodesignProvision=",
        "/p:Version=$Version"
    )
    $source = Join-Path $Root "src/Hello1Drive.iOS/bin/$Configuration/net10.0-ios26.0/iossimulator-arm64/publish"
    $targetDir = Join-Path $Publish "ios-simulator-arm64"
    Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item (Join-Path $source "*") $targetDir -Recurse -Force
    New-Zip $targetDir (Join-Path $Artifacts "Hello1Drive-iOS-Simulator-arm64-$Version.zip")
}

switch ($Target) {
    "desktop" { Publish-Desktop }
    "browser" { Publish-Browser }
    "android" { Publish-Android }
    "ios"     { Publish-iOS }
    "all"     { Publish-Desktop; Publish-Browser; Publish-Android; Publish-iOS }
}

Write-Host "`nPublish finished. Artifacts: $Artifacts" -ForegroundColor Green
