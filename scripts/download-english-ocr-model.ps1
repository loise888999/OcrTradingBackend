param(
    [string]$ModelRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
    $ModelRoot = Join-Path $repoRoot "Data\ocr-models\english"
}

$recDir = Join-Path $ModelRoot "rec"
$dictPath = Join-Path $ModelRoot "en_dict.txt"

New-Item -ItemType Directory -Force -Path $recDir | Out-Null

$files = @(
    @{
        Url = "https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec/resolve/main/inference.json"
        Path = Join-Path $recDir "inference.json"
    },
    @{
        Url = "https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec/resolve/main/inference.pdiparams"
        Path = Join-Path $recDir "inference.pdiparams"
    },
    @{
        Url = "https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec/resolve/main/inference.yml"
        Path = Join-Path $recDir "inference.yml"
    },
    @{
        Url = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/en_dict.txt"
        Path = $dictPath
    }
)

foreach ($file in $files) {
    Write-Host "Downloading $($file.Url)"
    Invoke-WebRequest -Uri $file.Url -OutFile $file.Path
}

$missing = @()

foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.Path -PathType Leaf)) {
        $missing += $file.Path
    }
}

if ($missing.Count -gt 0) {
    throw "Download finished, but these expected files were not found: $($missing -join ', ')"
}

Write-Host ""
Write-Host "English OCR model downloaded to:"
Write-Host "  $ModelRoot"
Write-Host ""
Write-Host "Downloaded files:"

foreach ($file in $files) {
    $item = Get-Item -LiteralPath $file.Path
    Write-Host ("  {0} ({1:N0} bytes)" -f $item.FullName, $item.Length)
}

Write-Host ""
Write-Host "To enable it, set these appsettings.json values:"
Write-Host '  "UseEnglishModels": true,'
Write-Host '  "FallbackToBundledModel": true,'
Write-Host '  "RecognitionModelPath": "Data/ocr-models/english/rec",'
Write-Host '  "DictionaryPath": "Data/ocr-models/english/en_dict.txt"'
