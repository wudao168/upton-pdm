param([string]$ProjectRoot=(Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))))
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\','/')
$manifestPath=Join-Path $repoRoot 'src\pdm-ui\public\plm-brand-assets.json'
if(-not(Test-Path -LiteralPath $manifestPath)){throw '缺少统一图标清单，请先运行 pnpm --dir src/pdm-ui generate:icons。'}
$manifest=[IO.File]::ReadAllText($manifestPath)|ConvertFrom-Json
function Get-IconHash($file){
  $algorithm=[Security.Cryptography.SHA256]::Create()
  try{return [BitConverter]::ToString($algorithm.ComputeHash([IO.File]::ReadAllBytes($file))).Replace('-','')}
  finally{$algorithm.Dispose()}
}
foreach($item in @($manifest.source,$manifest.wordmark,$manifest.generator)+@($manifest.outputs)){
  $file=[IO.Path]::GetFullPath((Join-Path $repoRoot $item.path))
  if(-not $file.StartsWith($repoRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '图标素材路径超出项目目录。'}
  if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-IconHash $file) -ne $item.sha256){throw ('图标素材不同步，请先运行 pnpm --dir src/pdm-ui generate:icons：'+$item.path)}
}
Write-Output ('统一图标校验通过：'+$manifest.source.sha256)
