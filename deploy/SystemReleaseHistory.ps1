function Get-UplmReleaseNote {
    param(
        [string]$Version,
        [string]$ReleaseNote,
        [string]$Fallback = '历史发布未填写说明。'
    )

    if (-not [string]::IsNullOrWhiteSpace($ReleaseNote)) { return (ConvertFrom-UplmMojibake $ReleaseNote).Trim() }
    $separator = $Version.IndexOf('-')
    if ($separator -lt 0) { return $Fallback }
    $tag = $Version.Substring($separator + 1).Trim()
    if ($tag -eq 'version-information') { return '增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。' }
    if ([string]::IsNullOrWhiteSpace($tag)) { return $Fallback }
    return $tag
}

function Get-UplmReleasedAt {
    param(
        [string]$Version,
        [object]$ReleasedAt
    )

    if ($ReleasedAt -is [DateTimeOffset]) { return $ReleasedAt.ToString('O') }
    if ($ReleasedAt -is [DateTime]) { return ([DateTimeOffset]$ReleasedAt).ToString('O') }
    $releasedAtText = [string]$ReleasedAt
    if (-not [string]::IsNullOrWhiteSpace($releasedAtText)) { return $releasedAtText.Trim() }
    if ($Version -match '^(\d{4})\.(\d{2})\.(\d{2})\.(\d{2})(\d{2})') {
        return "$($Matches[1])-$($Matches[2])-$($Matches[3])T$($Matches[4]):$($Matches[5]):00+08:00"
    }
    return [DateTimeOffset]::Now.ToString('O')
}

function Get-UplmPropertyValue {
    param(
        [object]$InputObject,
        [string]$Name
    )

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertFrom-UplmMojibake {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $Value }
    $current = $Value
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $windows1252 = [Text.Encoding]::GetEncoding(1252)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if ($current -match '[\u3400-\u9fff]') { break }
        try {
            $bytes = $windows1252.GetBytes($current)
            $decoded = $strictUtf8.GetString($bytes)
        }
        catch { break }
        if ($decoded -eq $current -or $decoded -notmatch '[\u3400-\u9fff]') { break }
        $current = $decoded
    }
    return $current
}

function Get-UplmJsonUtf8 {
    param([Parameter(Mandatory = $true)][string]$Uri)

    $client = New-Object Net.WebClient
    try {
        $bytes = $client.DownloadData($Uri)
        return ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
    }
    finally {
        $client.Dispose()
    }
}

function Get-UplmReleaseHistory {
    param(
        [string]$CurrentVersion,
        [string]$CurrentReleaseNote,
        [string]$CurrentDesktopVersion,
        [string]$CurrentSolidWorksAddinVersion,
        [object[]]$SourceBootstraps = @(),
        [string]$BaselinePath = ''
    )

    $candidates = New-Object System.Collections.Generic.List[object]
    $candidates.Add([pscustomobject][ordered]@{
        Version = $CurrentVersion
        ReleasedAt = Get-UplmReleasedAt -Version $CurrentVersion
        ReleaseNote = Get-UplmReleaseNote -Version $CurrentVersion -ReleaseNote $CurrentReleaseNote -Fallback '本次发布未填写版本说明。'
        DesktopVersion = $CurrentDesktopVersion
        SolidWorksAddinVersion = $CurrentSolidWorksAddinVersion
    })

    foreach ($bootstrap in @($SourceBootstraps)) {
        if ($null -eq $bootstrap) { continue }
        $sourceVersion = [string](Get-UplmPropertyValue -InputObject $bootstrap -Name 'ConfigurationVersion')
        if (-not [string]::IsNullOrWhiteSpace($sourceVersion)) {
            $desktop = Get-UplmPropertyValue -InputObject $bootstrap -Name 'Desktop'
            $addin = Get-UplmPropertyValue -InputObject $bootstrap -Name 'SolidWorksAddin'
            $candidates.Add([pscustomobject][ordered]@{
                Version = $sourceVersion
                ReleasedAt = Get-UplmReleasedAt -Version $sourceVersion -ReleasedAt (Get-UplmPropertyValue -InputObject $bootstrap -Name 'ReleasedAt')
                ReleaseNote = Get-UplmReleaseNote -Version $sourceVersion -ReleaseNote ([string](Get-UplmPropertyValue -InputObject $bootstrap -Name 'ReleaseNote'))
                DesktopVersion = [string](Get-UplmPropertyValue -InputObject $desktop -Name 'Version')
                SolidWorksAddinVersion = [string](Get-UplmPropertyValue -InputObject $addin -Name 'Version')
            })
        }
        foreach ($entry in @((Get-UplmPropertyValue -InputObject $bootstrap -Name 'ReleaseHistory'))) {
            if ($null -ne $entry) { $candidates.Add($entry) }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($BaselinePath) -and (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
        $baseline = Get-Content -LiteralPath $BaselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($entry in @((Get-UplmPropertyValue -InputObject $baseline -Name 'ReleaseHistory'))) {
            if ($null -ne $entry) { $candidates.Add($entry) }
        }
    }

    $seen = @{}
    $history = New-Object System.Collections.Generic.List[object]
    foreach ($candidate in $candidates) {
        $version = [string](Get-UplmPropertyValue -InputObject $candidate -Name 'Version')
        if ([string]::IsNullOrWhiteSpace($version)) { continue }
        $version = $version.Trim()
        $key = $version.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $history.Add([pscustomobject][ordered]@{
            Version = $version
            ReleasedAt = Get-UplmReleasedAt -Version $version -ReleasedAt (Get-UplmPropertyValue -InputObject $candidate -Name 'ReleasedAt')
            ReleaseNote = Get-UplmReleaseNote -Version $version -ReleaseNote ([string](Get-UplmPropertyValue -InputObject $candidate -Name 'ReleaseNote'))
            DesktopVersion = [string](Get-UplmPropertyValue -InputObject $candidate -Name 'DesktopVersion')
            SolidWorksAddinVersion = [string](Get-UplmPropertyValue -InputObject $candidate -Name 'SolidWorksAddinVersion')
        })
    }

    return @($history | Sort-Object -Property @{ Expression = {
        if ($_.Version -match '^(\d{4})\.(\d{2})\.(\d{2})\.(\d{2})(\d{2})') {
            return [long]("$($Matches[1])$($Matches[2])$($Matches[3])$($Matches[4])$($Matches[5])")
        }
        return 0
    }; Descending = $true })
}
