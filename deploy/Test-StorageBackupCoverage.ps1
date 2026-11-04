[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StorageBackup.Common.ps1')
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $temporaryParent ('pdm-storage-coverage-' + [Guid]::NewGuid().ToString('N'))
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw '应拒绝无效或不完整的备份，但操作意外成功。' }
}
try {
    $vault = Join-Path $root 'old-vault'
    $attachments = Join-Path $root 'attachments'
    $destination = Join-Path $root 'backup'
    $sourceFiles = @((Join-Path $vault '.versions\v1\原始图档.SLDPRT'), (Join-Path $vault '.markups\review.markup'), (Join-Path $attachments 'technical.pdf'))
    foreach ($path in $sourceFiles) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
        [IO.File]::WriteAllText($path, '完整历史档案：' + $path)
    }
    [IO.File]::SetAttributes($sourceFiles[0], [IO.FileAttributes]::ReadOnly)
    $references = @($sourceFiles | ForEach-Object { [pscustomobject]@{ path = $_; length = (Get-Item -LiteralPath $_).Length; sha256 = (Get-FileHash -LiteralPath $_).Hash; category = '历史资料' } })
    $plan = [pscustomobject]@{ sources = @(
        [pscustomobject]@{ path = $vault; categories = @('图档与批注'); isDirectory = $true; required = $true },
        [pscustomobject]@{ path = (Join-Path $vault '.versions'); categories = @('重叠历史位置'); isDirectory = $true; required = $true },
        [pscustomobject]@{ path = $attachments; categories = @('附件'); isDirectory = $true; required = $true }
    ); references = $references }
    $manifest = Copy-PdmStorageSources $plan $destination
    if ($manifest.sources.Count -ne 2 -or $manifest.files.Count -ne 3 -or $manifest.references.Count -ne 3) { throw '备份覆盖范围或去重结果不正确。' }
    Test-PdmStorageBackupFiles $manifest $destination
    if (-not (Test-Path -LiteralPath $sourceFiles[0])) { throw '测试期间不应移除源档案。' }
    Expect-Failure { Resolve-PdmBackupPath $destination '..\outside.txt' }
    $missing = [pscustomobject]@{ sources = $plan.sources; references = @($references) + @([pscustomobject]@{ path = (Join-Path $attachments 'missing.pdf'); length = 1; sha256 = 'A'; category = '缺失档案' }) }
    Expect-Failure { Copy-PdmStorageSources $missing (Join-Path $root 'incomplete') }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'incomplete\files-index.json'))) { throw '失败批次未保留已复制文件的路径及哈希清单。' }
    $tampered = Resolve-PdmBackupPath $destination $manifest.files[0].relativePath
    [IO.File]::SetAttributes($tampered, [IO.FileAttributes]::Normal)
    [IO.File]::WriteAllText($tampered, '被更改的副本')
    Expect-Failure { Test-PdmStorageBackupFiles $manifest $destination }
    Write-Output '通过：真实历史目录、批注及附件覆盖；嵌套来源去重；源文件保留；缺失引用、越界路径及副本篡改均被拒绝。'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith($temporaryParent + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'pdm-storage-coverage-*') { throw '测试清理目标超出了隔离目录。' }
    if (Test-Path -LiteralPath $resolved) {
        Get-ChildItem -LiteralPath $resolved -File -Recurse -Force | ForEach-Object { [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal) }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
