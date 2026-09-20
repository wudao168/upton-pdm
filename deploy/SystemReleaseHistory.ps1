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
    $windows1252Bytes = @{
        0x20AC = 0x80; 0x201A = 0x82; 0x0192 = 0x83; 0x201E = 0x84; 0x2026 = 0x85; 0x2020 = 0x86; 0x2021 = 0x87
        0x02C6 = 0x88; 0x2030 = 0x89; 0x0160 = 0x8A; 0x2039 = 0x8B; 0x0152 = 0x8C; 0x017D = 0x8E; 0x2018 = 0x91
        0x2019 = 0x92; 0x201C = 0x93; 0x201D = 0x94; 0x2022 = 0x95; 0x2013 = 0x96; 0x2014 = 0x97; 0x02DC = 0x98
        0x2122 = 0x99; 0x0161 = 0x9A; 0x203A = 0x9B; 0x0153 = 0x9C; 0x017E = 0x9E; 0x0178 = 0x9F
    }
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        if ($current -match '[\u3400-\u9fff]') { break }
        $bytes = New-Object 'System.Collections.Generic.List[byte]'
        $mappable = $true
        foreach ($character in $current.ToCharArray()) {
            $codePoint = [int][char]$character
            if ($codePoint -le 0xFF) { $bytes.Add([byte]$codePoint) }
            elseif ($windows1252Bytes.ContainsKey($codePoint)) { $bytes.Add([byte]$windows1252Bytes[$codePoint]) }
            else { $mappable = $false; break }
        }
        if (-not $mappable) { break }
        $decoded = ConvertFrom-UplmUtf8Tolerant -Bytes $bytes.ToArray()
        if ($decoded -eq $current) { break }
        $current = $decoded
    }
    return $current
}

function ConvertFrom-UplmUtf8Tolerant {
    param([byte[]]$Bytes)

    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $builder = New-Object Text.StringBuilder
    $index = 0
    while ($index -lt $Bytes.Length) {
        $lead = $Bytes[$index]
        $length = 1
        if ($lead -ge 0xC2 -and $lead -le 0xDF) { $length = 2 }
        elseif ($lead -ge 0xE0 -and $lead -le 0xEF) { $length = 3 }
        elseif ($lead -ge 0xF0 -and $lead -le 0xF4) { $length = 4 }
        if ($length -gt 1 -and ($index + $length) -le $Bytes.Length) {
            try {
                [void]$builder.Append($strictUtf8.GetString($Bytes, $index, $length))
                $index += $length
                continue
            }
            catch {
                # 该位置不是完整的 UTF-8 序列，按单字节保留。
            }
        }
        [void]$builder.Append([char]$lead)
        $index++
    }
    return $builder.ToString()
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
        $version = (ConvertFrom-UplmMojibake $version.Trim()).Trim()
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
