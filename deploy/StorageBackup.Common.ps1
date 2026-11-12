$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StorageBackup.Runtime.ps1')

function ConvertTo-PdmRestorableSqlDump([AllowEmptyString()][string]$Sql) {
    # MySQL's version-comment wrapper can retain a single-statement trigger's
    # terminator inside the comment. The CLI then rejects the closing comment.
    # Anchor to mysqldump's CREATE/DEFINER/TRIGGER header; never rewrite row data
    # or the internal statements of BEGIN/END trigger bodies.
    $pattern = '(^[ \t]*/\*!\d+\s+CREATE\*/\s+/\*!\d+\s+DEFINER=(?:(?!\*/).)*?\*/\s+/\*!\d+\s+TRIGGER\b(?:(?!\*/).)*?\bFOR\s+EACH\s+ROW\s+)((?:(?!\*/).)*?)(\s*\*/;;)'
    $options = [Text.RegularExpressions.RegexOptions]::Multiline -bor [Text.RegularExpressions.RegexOptions]::Singleline -bor [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant
    $expression = [Text.RegularExpressions.Regex]::new($pattern, $options)
    return $expression.Replace($Sql, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $body = $match.Groups[2].Value
        $statement = $body.TrimEnd()
        if ($statement -notmatch '^(SIGNAL|INSERT)\b' -or -not $statement.EndsWith(';')) { return $match.Value }
        return $match.Groups[1].Value + $statement.Substring(0, $statement.Length - 1) + $body.Substring($statement.Length) + $match.Groups[3].Value
    })
}

function Resolve-PdmMySqlTool([string]$ProjectRoot, $Receipt, [ValidateSet('mysql.exe', 'mysqldump.exe')][string]$FileName) {
    $recordedHome = [string]$Receipt.mysqlHome
    if (-not [string]::IsNullOrWhiteSpace($recordedHome)) {
        $recordedTool = [IO.Path]::Combine($recordedHome, 'bin', $FileName)
        if ([IO.File]::Exists($recordedTool)) { return $recordedTool }
    }
    $version = [string]$Receipt.mysqlVersion
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '部署记录中的数据库版本无效，请核对本机运行环境。' }
    $relocatedTool = Join-Path $ProjectRoot ('.runtime\mysql-' + $version + '-winx64\bin\' + $FileName)
    if (-not [IO.File]::Exists($relocatedTool)) { throw '未找到部署记录对应的数据库工具，请核对本机运行目录。' }
    Assert-PdmOrdinaryBackupPath $relocatedTool
    return [IO.Path]::GetFullPath($relocatedTool)
}
function Resolve-PdmBackupPath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathFullyQualified($Relative)) { throw '备份清单含有绝对目标路径，已停止。' }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '备份清单路径超出了备份目录，已停止。' }
    return $path
}

function Assert-PdmOrdinaryBackupPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "目录包含链接或重解析点，请先核对实际位置：$current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-PdmStorageBackupPlan([string]$ProjectRoot, [switch]$IncludeTableCounts, [string]$Database) {
    $local = Join-Path $ProjectRoot '.local'
    $api = Join-Path $local 'api'
    $dotnet = Join-Path $ProjectRoot '.dotnet\dotnet.exe'
    $dll = Join-Path $api 'Pdm.Api.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw '未找到已部署的API，请先部署支持集中存储设置的版本。' }
    $savedEnvironment = @{}
    $serviceEnvironment = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\UptonPdmApi' -Name Environment -ErrorAction SilentlyContinue).Environment)
    try {
        foreach ($entry in $serviceEnvironment) {
            if ($entry -notmatch '^(PDM_[^=]+|Pdm__[^=]+|ASPNETCORE_ENVIRONMENT)=(.*)$') { continue }
            $key = $Matches[1]; $value = $Matches[2]
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        }
        if (-not $env:PDM_DB_PASSWORD -and @($serviceEnvironment | Where-Object { $_ }).Count -eq 0) {
            # 本机启动脚本使用私有配置文件，未注册Windows服务时沿用同一组配置。
            $secretPath = Join-Path $local 'secrets\pdm-secrets.json'
            if (Test-Path -LiteralPath $secretPath -PathType Leaf) {
                $secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $localEnvironment = @{
                    'ASPNETCORE_ENVIRONMENT' = 'Production'
                    'PDM_DB_PASSWORD' = [string]$secrets.databasePassword
                    'PDM_JWT_SIGNING_KEY' = [string]$secrets.jwtSigningKey
                    'Pdm__Storage__UploadTempRoot' = Join-Path $local 'uploads'
                    'Pdm__Storage__ProgramTemplateRoot' = Join-Path $local 'program-templates'
                    'Pdm__PreviewWorker__AllowLocalExecution' = 'false'
                }
                foreach ($key in $localEnvironment.Keys) {
                    if ([Environment]::GetEnvironmentVariable($key, 'Process')) { continue }
                    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
                    [Environment]::SetEnvironmentVariable($key, $localEnvironment[$key], 'Process')
                }
            }
        }
        if (-not $env:PDM_DB_PASSWORD) { throw '缺少API服务的数据库配置，不能确认实际备份范围。' }
        if ($Database) {
            if ($Database -notmatch '^[A-Za-z0-9_]+_qa$') { throw '恢复验证只允许使用隔离验收数据库。' }
            if (-not $savedEnvironment.ContainsKey('PDM_DATABASE_NAME')) { $savedEnvironment['PDM_DATABASE_NAME'] = $env:PDM_DATABASE_NAME }
            $env:PDM_DATABASE_NAME = $Database
        }
        Push-Location $api
        try {
            $arguments = @($dll, '--storage-backup-plan')
            if ($IncludeTableCounts) { $arguments += '--storage-backup-table-counts' }
            $output = (& $dotnet @arguments | Out-String)
            if ($LASTEXITCODE -ne 0) { throw '读取实际存储清单失败；未开始备份，请检查服务器诊断日志。' }
            $plan = $output | ConvertFrom-Json
            if ($plan.format -ne 'upton-pdm-backup-plan-v2') { throw '服务器返回的存储清单格式不受支持。' }
            return $plan
        } finally { Pop-Location }
    } finally {
        foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    }
}

function Copy-PdmStorageSources($Plan, [string]$BackupRoot) {
    Assert-PdmOrdinaryBackupPath $BackupRoot
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    $selected = [Collections.Generic.List[object]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $index = @{}
    foreach ($source in @($Plan.sources | Sort-Object { $_.path.Length })) {
        $path = [IO.Path]::GetFullPath($source.path).TrimEnd('\', '/')
        if (-not (Test-Path -LiteralPath $path)) {
            if ($source.required) { throw "数据库引用的存档目录或配置文件缺失：$path" }
            continue
        }
        Assert-PdmOrdinaryBackupPath $path
        if ($selected | Where-Object { $_.isDirectory -and ($path + '\').StartsWith($_.path + '\', [StringComparison]::OrdinalIgnoreCase) }) { continue }
        $relative = 'data/source-{0:D4}' -f $selected.Count
        $target = Resolve-PdmBackupPath $BackupRoot $relative
        $mapping = [ordered]@{ path = $path; isDirectory = [bool]$source.isDirectory; relativePath = $relative; categories = $source.categories; acl = (Get-Acl -LiteralPath $path).Sddl }
        $selected.Add([pscustomobject]$mapping)
        # Keep a durable source map even if a later file or historical reference is inconsistent.
        $selected.ToArray() | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $BackupRoot 'source-map.json') -Encoding UTF8
        $items = if ($source.isDirectory) { @(Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction Stop) } else { @(Get-Item -LiteralPath $path -Force) }
        foreach ($item in $items) {
            Assert-PdmOrdinaryBackupPath $item.FullName
            $fileRelative = if ($source.isDirectory) { $relative + '/' + [IO.Path]::GetRelativePath($path, $item.FullName).Replace('\', '/') } else { $relative }
            $destination = Resolve-PdmBackupPath $BackupRoot $fileRelative
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            $length = $item.Length
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            Copy-Item -LiteralPath $item.FullName -Destination $destination -Force
            if ((Get-Item -LiteralPath $destination).Length -ne $length -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
                throw "文件复制后校验失败，备份尚未完成：$($item.FullName)"
            }
            $record = [pscustomobject]@{ relativePath = $fileRelative; length = $length; sha256 = $hash; attributes = [int]$item.Attributes; lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('O') }
            $files.Add($record)
            $index[$item.FullName.ToLowerInvariant()] = $record
        }
    }
    @{ sources = $selected.ToArray(); files = $files.ToArray() } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $BackupRoot 'files-index.json') -Encoding UTF8
    $references = [Collections.Generic.List[object]]::new()
    foreach ($reference in $Plan.references) {
        $key = [IO.Path]::GetFullPath($reference.path).ToLowerInvariant()
        $record = $index[$key]
        if ($null -eq $record) { throw "数据库引用的历史文件未进入备份：$($reference.path)" }
        if ([long]$record.length -ne [long]$reference.length -or ($reference.sha256 -and $record.sha256 -ne $reference.sha256)) {
            throw "文件与数据库版本记录不一致，备份尚未完成：$($reference.path)"
        }
        $references.Add([pscustomobject]@{ sourcePath = $reference.path; relativePath = $record.relativePath; length = $record.length; sha256 = $record.sha256; category = $reference.category })
    }
    return [pscustomobject]@{ sources = $selected.ToArray(); files = $files.ToArray(); references = $references.ToArray() }
}

function Test-PdmStorageBackupFiles($Manifest, [string]$Root) {
    $lookup = @{}
    foreach ($file in $Manifest.files) {
        $path = Resolve-PdmBackupPath $Root $file.relativePath
        Assert-PdmOrdinaryBackupPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "备份文件缺失：$($file.relativePath)" }
        if ((Get-Item -LiteralPath $path).Length -ne [long]$file.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "备份文件校验失败：$($file.relativePath)"
        }
        $lookup[$file.relativePath] = $file
    }
    foreach ($reference in @($Manifest.references | Where-Object { $null -ne $_ })) {
        $file = $lookup[$reference.relativePath]
        if ($null -eq $file -or [long]$file.length -ne [long]$reference.length -or $file.sha256 -ne $reference.sha256) {
            throw "数据库文件引用未能映射到恢复副本：$($reference.relativePath)"
        }
    }
}
