param(
    [string]$SdkRoot = "$PSScriptRoot\..\.tools\android-sdk",
    [string]$JavaRoot = "$PSScriptRoot\..\.tools\java\jdk-21.0.13.0.2+1",
    [string]$ZxingJar = "$PSScriptRoot\..\.tools\android-downloads\zxing-core.jar",
    [int]$VersionCode = 2,
    [string]$VersionName = '1.0.1'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$sdk = (Resolve-Path $SdkRoot).Path
$java = (Resolve-Path $JavaRoot).Path
$zxing = (Resolve-Path $ZxingJar).Path
$env:JAVA_HOME = $java
$tools = Join-Path $sdk 'build-tools\35.0.0'
$android = Join-Path $sdk 'platforms\android-35\android.jar'
$source = Join-Path $repo 'src\Pdm.Production.Android\app\src\main'
$work = Join-Path $repo ('.local\production-android\build-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$output = Join-Path $repo 'artifacts\production-app'
New-Item -ItemType Directory -Force $work,$output,"$work\generated","$work\classes","$work\dex","$work\assets\production-app" | Out-Null
function Check-Exit([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step 失败，退出码 $LASTEXITCODE" } }
Copy-Item -Path "$repo\src\pdm-ui\public\production-app\*" -Destination "$work\assets\production-app"
[xml]$manifest = [IO.File]::ReadAllText("$source\AndroidManifest.xml")
$manifest.manifest.SetAttribute('package', 'com.upton.pdm.production')
$manifest.Save("$work\AndroidManifest.xml")
& "$tools\aapt2.exe" compile --dir "$source\res" -o "$work\resources.zip"
Check-Exit '编译图标'
& "$tools\aapt2.exe" link -o "$work\unsigned.apk" -I $android --manifest "$work\AndroidManifest.xml" --java "$work\generated" --min-sdk-version 28 --target-sdk-version 35 --version-code $VersionCode --version-name $VersionName "$work\resources.zip"
Check-Exit '打包界面资源'
# Windows aapt2 can emit backslashes in asset ZIP names; jar uses Android's required slash paths.
& "$java\bin\jar.exe" --update --file "$work\unsigned.apk" -C $work assets
Check-Exit '写入移动页面资源'
$config = Join-Path $work 'generated\com\upton\pdm\production\BuildConfig.java'
[IO.File]::WriteAllText($config, 'package com.upton.pdm.production; public final class BuildConfig { public static final boolean DEBUG = false; }')
$files = @(Get-ChildItem "$source\java","$work\generated" -Filter '*.java' -Recurse | ForEach-Object { '"' + $_.FullName.Replace('\','/') + '"' })
[IO.File]::WriteAllLines("$work\sources.txt", $files, [Text.UTF8Encoding]::new($false))
& "$java\bin\javac.exe" -encoding UTF-8 --release 17 -classpath "$android;$zxing" -d "$work\classes" "@$work\sources.txt"
Check-Exit '编译 Android 代码'
& "$java\bin\jar.exe" --create --file "$work\classes.jar" -C "$work\classes" .
Check-Exit '整理编译结果'
& "$tools\d8.bat" --release --min-api 28 --lib $android --output "$work\dex" "$work\classes.jar" $zxing
Check-Exit '生成 Android 字节码'
& "$java\bin\jar.exe" --update --file "$work\unsigned.apk" -C "$work\dex" classes.dex
Check-Exit '写入 Android 字节码'
& "$tools\zipalign.exe" -f -p 4 "$work\unsigned.apk" "$work\aligned.apk"
Check-Exit '对齐 APK'
$key = Join-Path $repo '.local\production-android\local-test.jks'
if (-not (Test-Path -LiteralPath $key)) {
    & "$java\bin\keytool.exe" -genkeypair -keystore $key -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=PDM Local Test,O=UPTON,C=CN'
    Check-Exit '生成本机测试签名'
}
$apk = Join-Path $output ("PDM生产执行-$VersionName-本机测试.apk")
& "$tools\apksigner.bat" sign --ks $key --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out $apk "$work\aligned.apk"
Check-Exit '签名 APK'
& "$tools\apksigner.bat" verify --verbose $apk
Check-Exit '验证 APK 签名'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($apk)
try {
    foreach ($name in @('index.html','app.js','app.css','logo.png')) {
        $entry = $archive.GetEntry('assets/production-app/' + $name)
        if ($null -eq $entry) { throw "APK 缺少可供 Android 读取的资源：$name" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')
            if ($hash -ne (Get-FileHash -LiteralPath "$repo/src/pdm-ui/public/production-app/$name").Hash) { throw "APK 页面资源与源码不一致：$name" }
        } finally { $stream.Dispose(); $sha.Dispose() }
    }
} finally { $archive.Dispose() }
& "$tools\aapt2.exe" dump badging $apk | Select-String 'package:|sdkVersion:|targetSdkVersion:|application-label:|launchable-activity:'
Get-FileHash -LiteralPath $apk -Algorithm SHA256 | Select-Object Path,Hash
