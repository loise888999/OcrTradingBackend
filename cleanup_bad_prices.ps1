param(
    [string]$DatabasePath = ".\ocr-trading.db"
)

if (-not (Test-Path $DatabasePath)) {
    Write-Host "Database not found: $DatabasePath" -ForegroundColor Red
    Write-Host "Pass the correct path, example:"
    Write-Host "  .\cleanup_bad_prices.ps1 -DatabasePath .\Data\ocr-trading.db"
    exit 1
}

$sql = @"
SELECT Id, City, ItemName, TradeGoodType, TradeType, Price, CapturedAtUtc
FROM PriceCaptures
WHERE Price > 2147483647 OR Price < 0
ORDER BY CapturedAtUtc DESC;

DELETE FROM PriceCaptures
WHERE Price > 2147483647 OR Price < 0;

SELECT COUNT(*) AS BadPriceRowsRemaining
FROM PriceCaptures
WHERE Price > 2147483647 OR Price < 0;
"@

Write-Host "Cleaning bad prices in $DatabasePath ..." -ForegroundColor Cyan
$sql | sqlite3 $DatabasePath
Write-Host "Done." -ForegroundColor Green
