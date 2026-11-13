function Get-UplmUpdateSigningCertificate {
    $thumbprint = ($env:UPLM_UPDATE_SIGNING_CERTIFICATE -replace '\s', '')
    if ($thumbprint -notmatch '^[0-9a-fA-F]{40}$') {
        throw '请配置 UPLM_UPDATE_SIGNING_CERTIFICATE 为当前 Windows 用户证书库中的发布签名证书指纹。'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
    if (!$certificate.HasPrivateKey -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -lt (Get-Date)) {
        throw '发布证书缺少私钥、尚未生效或已过期。'
    }
    return $certificate
}

function Export-UplmUpdatePublicKey($Certificate, [string]$Path) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if ($null -eq $rsa) { throw '发布证书必须使用 RSA 密钥。' }
    try {
        if ($rsa.KeySize -lt 2048) { throw '发布证书的 RSA 密钥长度至少为 2048 位。' }
        [IO.File]::WriteAllText($Path, $rsa.ToXmlString($false), [Text.UTF8Encoding]::new($false))
    } finally { $rsa.Dispose() }
}

function Get-UplmUpdateSignature($Certificate, [string]$Component, [string]$Version, [string]$Sha256) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) { throw '发布证书必须包含 RSA 私钥。' }
    try {
        $payload = [Text.Encoding]::UTF8.GetBytes("UPLM-UPDATE-V1`n$Component`n$Version`n$($Sha256.ToUpperInvariant())")
        $signature = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        return [Convert]::ToBase64String($signature)
    } finally { $rsa.Dispose() }
}
