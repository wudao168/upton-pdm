param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

function Await-WinRt($Operation, [Type]$ResultType) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 } |
        Select-Object -First 1
    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

function Await-WinRtAction($Operation) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 } |
        Select-Object -First 1
    $task = $method.Invoke($null, @($Operation))
    $task.Wait()
}

[void][Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
[void][Windows.Storage.FileAccessMode, Windows.Storage, ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime]
[void][Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType=WindowsRuntime]

function Read-OcrStream($Stream, $Engine) {
    $Stream.Seek(0)
    $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($Stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    try {
        $result = Await-WinRt ($Engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        return $result.Text
    }
    finally {
        $bitmap.Dispose()
    }
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$file = Await-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync($resolvedPath)) ([Windows.Storage.StorageFile])
$language = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Where-Object { $_.LanguageTag -match '^zh' } | Select-Object -First 1
if ($null -eq $language) { $language = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Select-Object -First 1 }
if ($null -eq $language) { throw 'Windows OCR language pack is not available.' }
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if ($null -eq $engine) { throw "Windows OCR engine is not available for $($language.LanguageTag)." }

$extension = [System.IO.Path]::GetExtension($resolvedPath).ToLowerInvariant()
$pages = New-Object System.Collections.Generic.List[string]
if ($extension -eq '.pdf') {
    $pdf = Await-WinRt ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    if ($pdf.PageCount -gt 20) { throw "PDF has $($pdf.PageCount) pages and exceeds the 20 page recognition limit." }
    for ($index = 0; $index -lt $pdf.PageCount; $index++) {
        $page = $pdf.GetPage([uint32]$index)
        $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
        $options = [Windows.Data.Pdf.PdfPageRenderOptions]::new()
        $options.DestinationWidth = 1600
        try {
            Await-WinRtAction ($page.RenderToStreamAsync($stream, $options))
            $pages.Add((Read-OcrStream $stream $engine))
        }
        finally {
            $stream.Dispose()
            $page.Dispose()
        }
    }
}
elseif ($extension -in @('.png', '.jpg', '.jpeg')) {
    $stream = Await-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    try { $pages.Add((Read-OcrStream $stream $engine)) } finally { $stream.Dispose() }
}
else {
    throw 'Only PDF, PNG, JPG and JPEG files can be recognized.'
}

$text = ($pages | ForEach-Object { $_.Trim() }) -join "`r`n"
[System.IO.File]::WriteAllText($OutputPath, $text, [System.Text.UTF8Encoding]::new($false))
