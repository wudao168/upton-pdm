[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][string]$IpAddress,
    [ValidateRange(1024,65535)][int]$Port = 5443
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请以管理员身份配置扫码HTTPS。' }
$ip = [Net.IPAddress]::Parse($IpAddress)
if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw '请提供当前内网服务器的IPv4地址。' }
$root = [IO.Path]::GetFullPath($ProjectRoot)
$settingsPath = Join-Path $root '.local/api/appsettings.json'
$web = Join-Path $root '.local/api/wwwroot'
$stateRoot = Join-Path $root '.local/production-scan-https'
$metadataPath = Join-Path $stateRoot 'certificates.json'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$before = [IO.File]::ReadAllText($settingsPath)
$settings = $before | ConvertFrom-Json
if ($settings.Kestrel.Endpoints.ProductionScanHttps -and $settings.Kestrel.Endpoints.ProductionScanHttps.Url -ne "https://0.0.0.0:$Port") { throw '已有扫码HTTPS端口配置，请先核对，避免覆盖现有入口。' }

if (Test-Path -LiteralPath $metadataPath) {
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.IpAddress -ne $IpAddress) { throw '服务器IP与已有扫码证书不同，请先核对证书及手机信任配置。' }
    $ca = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($metadata.RootThumbprint)"
    if (-not $ca.HasPrivateKey -or $ca.NotAfter -lt (Get-Date).AddDays(40)) { throw '扫码根证书不可用或即将到期，请先由管理员更新并向手机下发证书。' }
    $leaf = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($metadata.ServerThumbprint)" -ErrorAction SilentlyContinue
} else {
    $ca = New-SelfSignedCertificate -Type Custom -Subject 'CN=UPLM内网扫码根证书' -FriendlyName 'UPLM内网扫码根证书' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage CertSign,CRLSign `
        -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
}
if (-not $leaf -or -not $leaf.HasPrivateKey -or $leaf.NotAfter -lt (Get-Date).AddDays(30)) {
    $subject = "UPLM Camera $IpAddress $([guid]::NewGuid().ToString('N').Substring(0,8))"
    $leaf = New-SelfSignedCertificate -Type Custom -Subject "CN=$subject" -FriendlyName 'UPLM内网摄像头扫码HTTPS' -Signer $ca `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage DigitalSignature,KeyEncipherment `
        -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddDays(365) `
        -TextExtension @("2.5.29.17={text}IPAddress=$IpAddress", '2.5.29.37={text}1.3.6.1.5.5.7.3.1', '2.5.29.19={critical}{text}ca=0')
}
# 签发密钥留在计算机证书存储中，不导出私钥。只公开供手机安装的根证书。
$metadata = [pscustomobject]@{IpAddress=$IpAddress;RootThumbprint=$ca.Thumbprint;ServerThumbprint=$leaf.Thumbprint;Port=$Port}
$metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
$service = Get-CimInstance Win32_Service -Filter "Name='UptonPdmApi'"
if ($service.StartName -and $service.StartName -notin @('LocalSystem','NT AUTHORITY\SYSTEM')) {
    $identity = if ($service.StartName -eq 'NT AUTHORITY\NetworkService') { 'NT AUTHORITY\NETWORK SERVICE' } else { $service.StartName }
    $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($leaf)
    try {
        $keyPath = Join-Path $env:ProgramData ("Microsoft\Crypto\Keys\" + $key.Key.UniqueName)
        $acl = Get-Acl -LiteralPath $keyPath
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'Read','Allow'))
        Set-Acl -LiteralPath $keyPath -AclObject $acl
    } finally { $key.Dispose() }
}
$endpoint = [pscustomobject]@{Url="https://0.0.0.0:$Port";Certificate=[pscustomobject]@{
    Subject=$leaf.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)
    Store='My';Location='LocalMachine';AllowInvalid=$true
}}
$settings.Kestrel.Endpoints | Add-Member -MemberType NoteProperty -Name ProductionScanHttps -Value $endpoint -Force
$ruleName = "UPLM-ProductionScan-Https-$Port"
if (-not (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName "UPLM生产扫码HTTPS ($Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
        -RemoteAddress @('LocalSubnet','10.0.0.0/8','172.16.0.0/12','192.168.0.0/16') -Profile Any | Out-Null
}
$backupPath = Join-Path $stateRoot ('appsettings-before-' + (Get-Date -Format yyyyMMdd-HHmmss) + '.json')
[IO.File]::WriteAllText($backupPath,$before)
if ([IO.File]::ReadAllText($settingsPath) -ne $before) { throw 'API配置刚发生变化，请重新核对后再启用HTTPS。' }
$changed = $false
try {
    $nextPath = $settingsPath + '.scan-next'
    [IO.File]::WriteAllText($nextPath,($settings | ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $nextPath -Destination $settingsPath -Force
    $changed = $true
    $healthy = $false
    for ($attempt=0;$attempt -lt 15;$attempt++) {
        try { if ((Invoke-RestMethod "https://${IpAddress}:$Port/health" -SkipCertificateCheck -TimeoutSec 2).status -eq 'ok') { $healthy=$true;break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $healthy) { throw 'HTTPS未能正常启动，已恢复原API配置。请核对证书权限和端口。' }
    if ((Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 5).status -ne 'ok') { throw '原HTTP服务健康检查未通过。' }
} catch {
    if ($changed) { [IO.File]::WriteAllText($settingsPath,$before,[Text.UTF8Encoding]::new($false)) }
    throw
}
$targets = @($web,(Join-Path $root '.local/client/ui'),(Join-Path $root '.local/solidworks-addin/ui'),
    (Join-Path $env:LOCALAPPDATA 'UPLM/client/ui'),(Join-Path $env:LOCALAPPDATA 'UPLM/solidworks-addin/ui')) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
$certificateBytes = $ca.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)
$sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificateBytes))
$access = [pscustomobject]@{httpsScanUrl="https://${IpAddress}:$Port/?productionScan=1";certificateUrl='./production-camera-ca.crt';
    androidCertificateUrl='./production-camera-ca.crt';certificateName='UPLM内网扫码根证书';certificateSha256=$sha256}
foreach ($target in $targets) {
    [IO.File]::WriteAllBytes((Join-Path $target 'production-camera-ca.cer'),$certificateBytes)
    [IO.File]::WriteAllBytes((Join-Path $target 'production-camera-ca.crt'),$certificateBytes)
    [IO.File]::WriteAllText((Join-Path $target 'production-camera-access.json'),($access | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
& (Join-Path $root 'deploy/Sync-LocalUiManifest.ps1') -ProjectRoot $root
& (Join-Path $root 'deploy/Sync-LocalUiManifest.ps1') -ProjectRoot $root -VerifyOnly
[pscustomobject]@{HttpsScanUrl=$access.httpsScanUrl;CertificateName=$access.certificateName;CertificateSha256=$sha256;
    CertificateExpires=$leaf.NotAfter.ToString('yyyy-MM-dd');RootTrustInstalledOnThisComputer=$false;DeviceTrustRequired=$true} | ConvertTo-Json
