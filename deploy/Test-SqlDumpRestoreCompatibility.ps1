[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StorageBackup.Common.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$receipt = Get-Content -LiteralPath (Join-Path $repo '.local/deployment-receipt.json') -Raw | ConvertFrom-Json
$mysql = Resolve-PdmMySqlTool $repo $receipt 'mysql.exe'
$sampleId = [Guid]::NewGuid().ToString('N')
$output = Join-Path $repo ('tmp/sql-dump-compatibility/' + $sampleId)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$source = @'
CREATE TABLE item(id INT PRIMARY KEY);
CREATE TABLE sources(id INT PRIMARY KEY);
CREATE TABLE relation_template(id INT PRIMARY KEY, main_id INT NOT NULL);
CREATE TABLE observed(id INT PRIMARY KEY);
INSERT INTO item VALUES(1);
DELIMITER ;;
/*!50003 CREATE*/ /*!50017 DEFINER=`root`@`localhost`*/ /*!50003 TRIGGER item_immutable BEFORE UPDATE ON item FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='immutable; preserve literal'; */;;
/*!50003 CREATE*/ /*!50017 DEFINER=`root`@`localhost`*/ /*!50003 TRIGGER relation_register_source BEFORE INSERT ON relation_template FOR EACH ROW INSERT IGNORE INTO sources(id)
    SELECT id FROM item WHERE id=NEW.main_id; */;;
/*!50003 CREATE*/ /*!50017 DEFINER=`root`@`localhost`*/ /*!50003 TRIGGER relation_observe AFTER INSERT ON relation_template FOR EACH ROW BEGIN
    INSERT INTO observed VALUES(NEW.id);
END */;;
DELIMITER ;
'@
$block = "BEGIN`n    INSERT INTO observed VALUES(NEW.id);`nEND */;;"
$data = "INSERT INTO notes VALUES('/*!50003 CREATE*/ /*!50017 DEFINER=root*/ /*!50003 TRIGGER fake BEFORE UPDATE ON item FOR EACH ROW SIGNAL SQLSTATE ''45000''; */;;');"
$unknown = '/*!50003 CREATE*/ /*!50017 DEFINER=`root`@`localhost`*/ /*!50003 TRIGGER custom BEFORE INSERT ON item FOR EACH ROW CALL preserved(); */;;'
$cases = 0
foreach ($ending in @("`n", "`r`n")) {
    $inputSql = $source.Replace("`r`n", "`n").Replace("`n", $ending)
    $result = ConvertTo-PdmRestorableSqlDump $inputSql
    if ($result -ceq $inputSql -or $result -notlike '*immutable; preserve literal*' -or !$result.Contains($block.Replace("`n", $ending))) { throw '触发器兼容转换改变了语句内容或复合块。' }
    if ((ConvertTo-PdmRestorableSqlDump $result) -cne $result) { throw '重复兼容转换不应再次改变内容。' }
    $cases += 2
}
if ((ConvertTo-PdmRestorableSqlDump $data) -cne $data -or (ConvertTo-PdmRestorableSqlDump $unknown) -cne $unknown) { throw '非触发器数据或未涉及的语法不应被改变。' }
$cases += 2
$sourcePath = Join-Path $output 'source.sql'
$normalizedPath = Join-Path $output 'compatible.sql'
[IO.File]::WriteAllText($sourcePath, $source, [Text.UTF8Encoding]::new($false))
$sourceHash = (Get-FileHash -LiteralPath $sourcePath).Hash
[IO.File]::WriteAllText($normalizedPath, (ConvertTo-PdmRestorableSqlDump $source), [Text.UTF8Encoding]::new($false))
# 原生客户端只负责导入，读回断言使用MySqlConnector，避免把CLI的空stdout当作查询结果。
$harnessProject = Join-Path $repo 'tools/Pdm.SqlDumpRestoreAcceptance/Pdm.SqlDumpRestoreAcceptance.csproj'
$harnessOutput = Join-Path $output 'harness'
$buildLog = Join-Path $output 'harness-build.log'
& dotnet build $harnessProject -o $harnessOutput --nologo --verbosity quiet *> $buildLog
if ($LASTEXITCODE -ne 0) { throw "隔离恢复验收工具构建失败，请查看：$buildLog" }
& dotnet (Join-Path $harnessOutput 'Pdm.SqlDumpRestoreAcceptance.dll') $repo $output $mysql | Out-Null
$validationPath = Join-Path $output 'mysql-restore-validation.json'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $validationPath)) { throw "真实数据库恢复验证失败，请查看：$output" }
$validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
if ($validation.status -ne 'passed' -or $validation.sources -ne 1 -or $validation.observed -ne 1 -or $validation.triggers -ne 3 -or
    !$validation.immutableUpdateRejected -or !$validation.originalItemRetained -or !$validation.originalSourceUnchanged -or !$validation.isolatedDatabaseDeleted) {
    throw "真实数据库恢复验证未全部通过，请查看：$validationPath"
}
if ((Get-FileHash -LiteralPath $sourcePath).Hash -ne $sourceHash) { throw '原始备份样本被改变。' }
[pscustomobject]@{Status='passed';NormalizationCases=$cases;RestoredTriggers=$validation.triggers;RelationSourceInserted=($validation.sources -eq 1);CompoundTriggerRetained=($validation.observed -eq 1);ImmutableUpdateRejected=$validation.immutableUpdateRejected;OriginalSourceUnchanged=$validation.originalSourceUnchanged;IsolatedDatabaseDeleted=$validation.isolatedDatabaseDeleted;Database=$validation.database;Evidence=$output} | ConvertTo-Json
