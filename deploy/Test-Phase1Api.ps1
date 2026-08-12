[CmdletBinding()]
param(
    [string]$Database = 'pdm_phase1_qa',
    [int]$ApiPort = 5180
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[A-Za-z0-9_]+_qa$') { throw '验收数据库名必须以_qa结尾。' }

$projectRoot = Split-Path -Parent $PSScriptRoot
$secretPath = Join-Path $projectRoot '.local\secrets\pdm-secrets.json'
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$acceptanceDll = Join-Path $projectRoot 'tools\Pdm.Acceptance\bin\Debug\net10.0\Pdm.Acceptance.dll'
$apiDll = Join-Path $projectRoot 'src\Pdm.Api\bin\Debug\net10.0\Pdm.Api.dll'
$qaRoot = Join-Path $projectRoot '.local\qa'
$vault = Join-Path $qaRoot 'vault'
$release = Join-Path $qaRoot 'release'
$uploads = Join-Path $qaRoot 'uploads'
$apiBase = "http://127.0.0.1:$ApiPort"
$secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
$receipt = Get-Content -LiteralPath (Join-Path $projectRoot '.local\deployment-receipt.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$mysql = Join-Path $receipt.mysqlHome 'bin\mysql.exe'
$rootClient = Join-Path $projectRoot '.local\secrets\mysql-root-client.ini'

foreach ($path in @($qaRoot, $vault, $release, $uploads)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }

@"
DROP DATABASE IF EXISTS $Database;
CREATE DATABASE $Database CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
GRANT ALL PRIVILEGES ON $Database.* TO 'pdm_app'@'127.0.0.1';
FLUSH PRIVILEGES;
"@ | & $mysql "--defaults-extra-file=$rootClient" --batch --raw
if ($LASTEXITCODE -ne 0) { throw '隔离验收数据库重建失败。' }

function Assert-Phase1([bool]$condition, [string]$message) {
    if (-not $condition) { throw "一期API验收失败：$message" }
}

function Invoke-PdmJson([string]$method, [string]$path, $body, $headers) {
    $parameters = @{
        Uri = "$apiBase$path"
        Method = $method
        UseBasicParsing = $true
        TimeoutSec = 30
    }
    if ($null -ne $headers) { $parameters.Headers = $headers }
    if ($null -ne $body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = $body | ConvertTo-Json -Depth 100 -Compress
    }
    return Invoke-RestMethod @parameters
}

function Write-QaFile([string]$path, [string]$content) {
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($content))
}

function Get-Sha256([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Send-PdmFile([string]$filePath, [string]$relativeTargetPath, $headers, [string]$projectId) {
    $info = Get-Item -LiteralPath $filePath
    $session = Invoke-PdmJson 'Post' '/api/uploads/sessions' @{
        projectId = $projectId
        fileName = $info.Name
        totalLength = $info.Length
        sha256 = Get-Sha256 $filePath
    } $headers
    Assert-Phase1 ($session.chunkSize -eq 16777216) '上传分块必须为16MB。'
    Invoke-RestMethod -Uri "$apiBase/api/uploads/sessions/$($session.id)/chunks/0" -Method Put -Headers $headers -InFile $filePath -ContentType 'application/octet-stream' -TimeoutSec 30 | Out-Null
    return Invoke-PdmJson 'Post' "/api/uploads/sessions/$($session.id)/complete" @{ relativeTargetPath = $relativeTargetPath } $headers
}

function Test-ResumableUpload($headers, [string]$projectId) {
    $chunkSize = 16777216
    $remainingSize = 1048576
    $totalSize = $chunkSize + $remainingSize
    $fullPath = Join-Path $qaRoot 'resume-full.bin'
    $chunk0Path = Join-Path $qaRoot 'resume-chunk-0.bin'
    $chunk1Path = Join-Path $qaRoot 'resume-chunk-1.bin'
    foreach ($definition in @(@($fullPath, $totalSize), @($chunk0Path, $chunkSize), @($chunk1Path, $remainingSize))) {
        $stream = [IO.File]::Open($definition[0], [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.SetLength([long]$definition[1]) } finally { $stream.Dispose() }
    }

    $session = Invoke-PdmJson 'Post' '/api/uploads/sessions' @{
        projectId = $projectId
        fileName = 'resume-full.bin'
        totalLength = $totalSize
        sha256 = Get-Sha256 $fullPath
    } $headers
    Assert-Phase1 ($session.chunkSize -eq $chunkSize) '断点续传会话分块不是16MB。'

    Invoke-RestMethod -Uri "$apiBase/api/uploads/sessions/$($session.id)/chunks/0" -Method Put -Headers $headers -InFile $chunk0Path -ContentType 'application/octet-stream' -TimeoutSec 60 | Out-Null
    $interrupted = Invoke-PdmJson 'Get' "/api/uploads/sessions/$($session.id)" $null $headers
    Assert-Phase1 ($interrupted.receivedLength -eq $chunkSize) '中断后会话未记录已上传16MB。'

    Invoke-RestMethod -Uri "$apiBase/api/uploads/sessions/$($session.id)/chunks/1" -Method Put -Headers $headers -InFile $chunk1Path -ContentType 'application/octet-stream' -TimeoutSec 60 | Out-Null
    $resumed = Invoke-PdmJson 'Get' "/api/uploads/sessions/$($session.id)" $null $headers
    Assert-Phase1 ($resumed.receivedLength -eq $totalSize) '恢复后会话未记录完整长度。'

    $stored = Invoke-PdmJson 'Post' "/api/uploads/sessions/$($session.id)/complete" @{ relativeTargetPath = ".resume-qa/$($session.id)/resume-full.bin" } $headers
    Assert-Phase1 ($stored.length -eq $totalSize -and $stored.sha256 -eq (Get-Sha256 $fullPath)) '断点续传完成后长度或SHA-256不正确。'
    return $true
}

$env:PDM_DB_PASSWORD = $secrets.databasePassword
$env:PDM_ACCEPTANCE_CONNECTION = "Server=127.0.0.1;Port=3308;Database=$Database;UserID=pdm_app;GuidFormat=Binary16;SslMode=None;AllowUserVariables=true;ConnectionTimeout=5;DefaultCommandTimeout=30"
$env:PDM_ACCEPTANCE_ADMIN_PASSWORD = $secrets.bootstrapAdminPassword
$env:PDM_ACCEPTANCE_VAULT = $vault
$env:PDM_ACCEPTANCE_RELEASE = $release
$env:PDM_ACCEPTANCE_SEED = '1'
& $dotnet $acceptanceDll | Out-Null
if ($LASTEXITCODE -ne 0) { throw '隔离验收数据库迁移或种子准备失败。' }

$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:DOTNET_ENVIRONMENT = 'Production'
$env:PDM_JWT_SIGNING_KEY = $secrets.jwtSigningKey
$env:PDM_BOOTSTRAP_ADMIN_PASSWORD = $secrets.bootstrapAdminPassword
$env:PDM_DATABASE_NAME = $Database
$env:PDM_HTTP_URL = $apiBase
$env:Pdm__Storage__UploadTempRoot = $uploads
$env:ConnectionStrings__Pdm = $env:PDM_ACCEPTANCE_CONNECTION
$apiArguments = '"{0}" --environment Production --Pdm:Database:Provider=MySql --Pdm:Database:RunMigrations=true' -f $apiDll
$apiStdout = Join-Path $qaRoot 'api-test.stdout.log'
$apiStderr = Join-Path $qaRoot 'api-test.stderr.log'
$apiProcess = Start-Process -FilePath $dotnet -ArgumentList $apiArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $apiStdout -RedirectStandardError $apiStderr

try {
    $stage = 'health'
    $health = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($apiProcess.HasExited) { throw "临时API提前退出，退出码$($apiProcess.ExitCode)。" }
        try { $health = Invoke-RestMethod -Uri "$apiBase/health" -TimeoutSec 2; if ($health.status -eq 'ok') { break } } catch {}
        Start-Sleep -Milliseconds 500
    }
    Assert-Phase1 ($null -ne $health -and $health.status -eq 'ok') '5180临时API健康检查失败。'
    Assert-Phase1 ($health.databaseName -eq $Database) "临时API数据库检查失败，响应=$($health | ConvertTo-Json -Compress)，期望$Database。"

    $stage = 'login-qa-admin'
    $login = Invoke-PdmJson 'Post' '/api/auth/login' @{ username = 'qa_admin'; password = $secrets.bootstrapAdminPassword } $null
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }
    $stage = 'login-qa-engineer'
    $engineerLogin = Invoke-PdmJson 'Post' '/api/auth/login' @{ username = 'qa_engineer'; password = $secrets.bootstrapAdminPassword } $null
    $engineerHeaders = @{ Authorization = "Bearer $($engineerLogin.accessToken)" }
    $stage = 'read-projects'
    $projects = @(Invoke-PdmJson 'Get' '/api/projects' $null $headers)
    $project = $projects | Where-Object { $_.code -eq 'QA-PHASE1' } | Select-Object -First 1
    Assert-Phase1 ($null -ne $project) '未读取到隔离验收项目。'
    $documents = @(Invoke-PdmJson 'Get' "/api/projects/$($project.id)/documents" $null $headers)
    $document = $documents | Where-Object { $_.fileName -eq 'QA-ROOT.SLDASM' } | Select-Object -First 1
    $root = Invoke-PdmJson 'Get' "/api/projects/$($project.id)/reference-tree" $null $headers
    Assert-Phase1 ($null -ne $document -and $root.documentId -eq $document.id) '项目图档与引用树读取失败。'

    $stage = 'resumable-upload'
    $resumableUploadPassed = Test-ResumableUpload $headers $project.id

    $stage = 'checkout-and-conflict'
    Invoke-PdmJson 'Post' "/api/documents/$($document.id)/checkout" $null $headers | Out-Null
    $conflictObserved = $false
    try { Invoke-PdmJson 'Post' "/api/documents/$($document.id)/checkout" $null $engineerHeaders | Out-Null } catch { if ($_.Exception.Response.StatusCode.value__ -eq 409) { $conflictObserved = $true } }
    Assert-Phase1 $conflictObserved '并发获取编辑权限未返回409冲突。'

    $stage = 'checkin-w1'
    $v1File = Join-Path $qaRoot 'QA-ROOT-v1.SLDASM'
    Write-QaFile $v1File 'UPTON-PDM-QA-VERSION-1'
    $v1Stored = Send-PdmFile $v1File ".versions/$($document.id)/$([Guid]::NewGuid().ToString('N'))/QA-ROOT.SLDASM" $headers $project.id
    $checkin1 = Invoke-PdmJson 'Post' "/api/documents/$($document.id)/checkin" @{
        projectId = $project.id; root = $root; comment = '一期验收W1'; storageRelativePath = $v1Stored.relativePath
        fileLength = $v1Stored.length; sha256 = $v1Stored.sha256; properties = @{ Material = '45#'; Description = '验收版本一' }
    } $headers
    Assert-Phase1 ($checkin1.version.revision.display -eq 'W1') '首次存档未生成W1。'

    $stage = 'checkin-w2'
    Invoke-PdmJson 'Post' "/api/documents/$($document.id)/checkout" $null $headers | Out-Null
    $v2File = Join-Path $qaRoot 'QA-ROOT-v2.SLDASM'
    Write-QaFile $v2File 'UPTON-PDM-QA-VERSION-2-CHANGED'
    $v2Stored = Send-PdmFile $v2File ".versions/$($document.id)/$([Guid]::NewGuid().ToString('N'))/QA-ROOT.SLDASM" $headers $project.id
    $checkin2 = Invoke-PdmJson 'Post' "/api/documents/$($document.id)/checkin" @{
        projectId = $project.id; root = $root; comment = '一期验收W2'; storageRelativePath = $v2Stored.relativePath
        fileLength = $v2Stored.length; sha256 = $v2Stored.sha256; properties = @{ Material = '铝'; Description = '验收版本二' }
    } $headers
    Assert-Phase1 ($checkin2.version.revision.display -eq 'W2') '后续存档未生成W2。'
    $stage = 'version-read-compare-restore'
    $comparison = Invoke-PdmJson 'Get' "/api/documents/$($document.id)/versions/compare?left=$($checkin1.version.id)&right=$($checkin2.version.id)" $null $headers
    Assert-Phase1 (@($comparison.propertyChanges).Count -ge 1) '版本属性对比未返回差异。'
    $download = Invoke-WebRequest -UseBasicParsing -Uri "$apiBase/api/documents/$($document.id)/versions/$($checkin1.version.id)/file?download=true" -Headers $headers -TimeoutSec 30
    Assert-Phase1 ($download.RawContentLength -eq $v1Stored.length) '历史版本读取长度不正确。'
    $restored = Invoke-PdmJson 'Post' "/api/documents/$($document.id)/versions/$($checkin1.version.id)/restore" @{ changeNote = '由W1恢复用于一期验收' } $headers
    Assert-Phase1 ($restored.version.revision.display -eq 'W3') '由W1恢复未生成W3。'

    $stage = 'bom-write-export'
    $electrical = @(@{ sequence = 1; drawingNumber = 'QA-E-001'; name = '验收电气件'; quantity = 3; unit = '件'; material = $null; specification = '24VDC'; revision = 'W1'; isComplete = $true })
    $savedBom = @(Invoke-PdmJson 'Put' "/api/projects/$($project.id)/boms/Electrical" @{ items = $electrical } $headers)
    Assert-Phase1 ($savedBom.Count -eq 1 -and $savedBom[0].quantity -eq 3) '电气BOM手工保存失败。'
    $xlsx = Invoke-WebRequest -UseBasicParsing -Uri "$apiBase/api/projects/$($project.id)/boms/Electrical/export" -Headers $headers -TimeoutSec 30
    Assert-Phase1 ($xlsx.RawContentLength -gt 1000) '电气BOM XLSX导出失败。'

    $stage = 'release-package-create-upload'
    $packageNumber = 'RP-QA-' + [DateTimeOffset]::Now.ToString('yyyyMMddHHmmssfff')
    $package = Invoke-PdmJson 'Post' '/api/release-packages' @{ projectId = $project.id; referenceSnapshotId = $null; number = $packageNumber; processReviewer = 'qa_admin'; approver = 'qa_admin' } $headers
    Assert-Phase1 ($package.state -eq 0) '发布包创建后不是草稿状态。'
    $pdf = Join-Path $qaRoot 'QA-DRAWING.pdf'; Write-QaFile $pdf "%PDF-1.4`nUPTON PDM QA`n%%EOF"
    $dwg = Join-Path $qaRoot 'QA-DRAWING.dwg'; Write-QaFile $dwg 'AC1027-UPTON-PDM-QA'
    Send-PdmFile $pdf ".release-staging/$packageNumber/drawings/QA-DRAWING.pdf" $headers $project.id | Out-Null
    Send-PdmFile $dwg ".release-staging/$packageNumber/drawings/QA-DRAWING.dwg" $headers $project.id | Out-Null
    $stage = 'release-package-submit-approve'
    $package = Invoke-PdmJson 'Post' "/api/release-packages/$($package.id)/submit" $null $headers
    Assert-Phase1 ($package.state -eq 1) '发布包提交后未进入工艺审核。'
    $processTask = @($package.approvalTasks) | Where-Object { $_.stage -eq 1 } | Select-Object -First 1
    $package = Invoke-PdmJson 'Post' "/api/approval-tasks/$($processTask.id)/decision" @{ decision = 0; comment = '工艺验收通过' } $headers
    Assert-Phase1 ($package.state -eq 2) '工艺审核后未进入批准。'
    $approvalTask = @($package.approvalTasks) | Where-Object { $_.stage -eq 2 } | Select-Object -First 1
    $package = Invoke-PdmJson 'Post' "/api/approval-tasks/$($approvalTask.id)/decision" @{ decision = 0; comment = '批准验收发布' } $headers
    Assert-Phase1 ($package.state -eq 5) '最终批准后未自动发布。'
    Assert-Phase1 (Test-Path -LiteralPath $package.publishedPath) '发布包未投放生产目录。'
    $requiredFiles = @('manifest.json', 'approval.json', 'checksums.sha256', 'mechanical-bom.xlsx', 'electrical-bom.xlsx', 'drawings\QA-DRAWING.pdf', 'drawings\QA-DRAWING.dwg')
    foreach ($relative in $requiredFiles) { Assert-Phase1 (Test-Path -LiteralPath (Join-Path $package.publishedPath $relative)) "发布包缺少$relative。" }

    $stage = 'formal-version-and-audit'
    $versionsAfterApproval = @(Invoke-PdmJson 'Get' "/api/documents/$($document.id)/versions" $null $headers)
    $released = $versionsAfterApproval | Where-Object { $_.revision.display -eq 'A' -and $_.releasePackageId -eq $package.id } | Select-Object -First 1
    Assert-Phase1 ($null -ne $released) '最终批准后未自动生成正式版本A。'

    $stage = 'reject-and-resubmit'
    $retryNumber = 'RP-QA-RETRY-' + [DateTimeOffset]::Now.ToString('yyyyMMddHHmmssfff')
    $retryPackage = Invoke-PdmJson 'Post' '/api/release-packages' @{ projectId = $project.id; referenceSnapshotId = $null; number = $retryNumber; processReviewer = 'qa_admin'; approver = 'qa_admin' } $headers
    Send-PdmFile $pdf ".release-staging/$retryNumber/drawings/QA-DRAWING.pdf" $headers $project.id | Out-Null
    Send-PdmFile $dwg ".release-staging/$retryNumber/drawings/QA-DRAWING.dwg" $headers $project.id | Out-Null
    $retryPackage = Invoke-PdmJson 'Post' "/api/release-packages/$($retryPackage.id)/submit" $null $headers
    $retryProcessTask = @($retryPackage.approvalTasks) | Where-Object { $_.stage -eq 1 } | Select-Object -First 1
    $retryPackage = Invoke-PdmJson 'Post' "/api/approval-tasks/$($retryProcessTask.id)/decision" @{ decision = 1; comment = '验收驳回' } $headers
    Assert-Phase1 ($retryPackage.state -eq 3) '驳回后发布包未进入已驳回状态。'
    $retryPackage = Invoke-PdmJson 'Post' "/api/release-packages/$($retryPackage.id)/submit" $null $headers
    Assert-Phase1 ($retryPackage.state -eq 1 -and @($retryPackage.approvalTasks | Where-Object { $null -ne $_.decision }).Count -eq 0) '重新提交未清空旧审批并回到工艺审核。'
    $retryProcessTask = @($retryPackage.approvalTasks) | Where-Object { $_.stage -eq 1 } | Select-Object -First 1
    $retryPackage = Invoke-PdmJson 'Post' "/api/approval-tasks/$($retryProcessTask.id)/decision" @{ decision = 0; comment = '重新提交后工艺通过' } $headers
    $retryApprovalTask = @($retryPackage.approvalTasks) | Where-Object { $_.stage -eq 2 } | Select-Object -First 1
    $retryPackage = Invoke-PdmJson 'Post' "/api/approval-tasks/$($retryApprovalTask.id)/decision" @{ decision = 0; comment = '重新提交后批准' } $headers
    Assert-Phase1 ($retryPackage.state -eq 5 -and (Test-Path -LiteralPath $retryPackage.publishedPath)) '重新提交后的发布包未自动投放。'

    $audit = @(Invoke-PdmJson 'Get' '/api/audit?take=200' $null $headers)
    Assert-Phase1 (($audit | Where-Object { $_.action -eq 'release-package.publish' }).Count -ge 1) '发布审计记录缺失。'
    Assert-Phase1 (($audit | Where-Object { $_.action -eq 'document.version.download' }).Count -ge 1) '历史版本下载审计记录缺失。'

    [pscustomobject]@{
        status = 'passed'
        database = $Database
        apiPort = $ApiPort
        project = $project.code
        versionFlow = @('W1', 'W2', 'W3', 'A')
        releasePackage = $package.number
        publishedPath = $package.publishedPath
        requiredFiles = $requiredFiles
        auditCount = $audit.Count
        concurrentCheckoutConflict = $conflictObserved
        resumableUpload = if ($resumableUploadPassed) { 'passed' } else { 'failed' }
        rejectionResubmission = 'passed'
    } | ConvertTo-Json -Depth 4
}
catch {
    $log = ((@(Get-Content -LiteralPath $apiStdout -Tail 20 -ErrorAction SilentlyContinue) + @(Get-Content -LiteralPath $apiStderr -Tail 20 -ErrorAction SilentlyContinue)) -join "`n")
    throw "一期API验收阶段[$stage]失败：$($_.Exception.Message)`n$log"
}
finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id -Force }
}
