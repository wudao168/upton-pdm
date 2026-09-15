function Get-UplmReleaseNote {
    param(
        [string]$Version,
        [string]$ReleaseNote,
        [string]$Fallback = '历史发布未填写说明。'
    )

    if (-not [string]::IsNullOrWhiteSpace($ReleaseNote)) { return $ReleaseNote.Trim() }
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
