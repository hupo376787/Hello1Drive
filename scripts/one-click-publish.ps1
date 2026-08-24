param(
    [ValidateSet("all", "desktop", "windows", "win-x64", "browser", "android", "ios")]
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
    param(
        [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
    )

    $project = Join-Path $Root "src/Hello1Drive.Desktop/Hello1Drive.Desktop.csproj"

    foreach ($rid in $Rids) {
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
                Write-Warning "After copying the .app from Windows to macOS, run: chmod +x Hello1Drive.app/Contents/MacOS/Hello1Drive. For release builds, package on macOS/CI."
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

    $source = Join-Path $Root "src/Hello1Drive.Android/bin/$Configuration/net10.0-android36.0/publish"
    $targetDir = Join-Path $Publish "android"
    Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

    $signedApk = Get-ChildItem $source -File -Filter "*-Signed.apk" | Select-Object -First 1
    $unsignedApk = Get-ChildItem $source -File -Filter "*.apk" |
        Where-Object { $_.Name -notlike "*-Signed.apk" } |
        Select-Object -First 1
    $signedAab = Get-ChildItem $source -File -Filter "*-Signed.aab" | Select-Object -First 1
    $unsignedAab = Get-ChildItem $source -File -Filter "*.aab" |
        Where-Object { $_.Name -notlike "*-Signed.aab" } |
        Select-Object -First 1

    if (-not $signedApk) {
        throw "Android publish output is missing the signed APK: $source"
    }

    $packages = @()
    $packages += [PSCustomObject]@{ Source = $signedApk; Name = "Hello1Drive-Android-$Version-Signed.apk" }
    if ($unsignedApk) {
        $packages += [PSCustomObject]@{ Source = $unsignedApk; Name = "Hello1Drive-Android-$Version.apk" }
    }
    if ($signedAab) {
        $packages += [PSCustomObject]@{ Source = $signedAab; Name = "Hello1Drive-Android-$Version-Signed.aab" }
    }
    if ($unsignedAab) {
        $packages += [PSCustomObject]@{ Source = $unsignedAab; Name = "Hello1Drive-Android-$Version.aab" }
    }

    # Remove package files from a previous Android publish so artifacts reflects this run exactly.
    Get-ChildItem $Artifacts -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "Hello1Drive-Android-$Version*.apk" -or $_.Name -like "Hello1Drive-Android-$Version*.aab" } |
        Remove-Item -Force
    Remove-Item (Join-Path $Artifacts "Hello1Drive-Android-$Version.zip") -Force -ErrorAction SilentlyContinue

    foreach ($package in $packages) {
        $publishPath = Join-Path $targetDir $package.Name
        $artifactPath = Join-Path $Artifacts $package.Name
        Copy-Item $package.Source.FullName $publishPath -Force
        Copy-Item $package.Source.FullName $artifactPath -Force
        Write-Host "Artifact: $artifactPath" -ForegroundColor Green
    }

    # APK/AAB are package formats already. Do not wrap them in another ZIP.
}

function Publish-iOS {
    Write-Host "`n=== iOS simulator ===" -ForegroundColor Yellow
    if (-not $IsMacOS) {
        Write-Warning "iOS publishing requires macOS + Xcode. iOS was skipped on this platform."
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
    "win-x64" { Publish-Desktop -Rids @("win-x64") }
    "windows" { Publish-Desktop -Rids @("win-x64", "win-arm64") }
    "desktop" { Publish-Desktop }
    "browser" { Publish-Browser }
    "android" { Publish-Android }
    "ios"     { Publish-iOS }
    "all"     { Publish-Desktop; Publish-Browser; Publish-Android; Publish-iOS }
}

Write-Host "`nPublish finished. Artifacts: $Artifacts" -ForegroundColor Green
