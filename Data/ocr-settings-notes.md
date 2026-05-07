# OCR Settings Notes

These notes explain the settings most useful for tuning OCR speed and accuracy.
Do not add comments directly to `appsettings.json`; JSON comments are invalid.

## Timing

- `DefaultIntervalMilliseconds`: How often the OCR background loop wakes up. Lower is more responsive and uses more CPU.
- `CityIntervalSeconds`: Minimum seconds between city OCR attempts.
- `PriceIntervalSeconds`: Normal minimum seconds between Buy/Sell price OCR attempts.
- `ActivePriceIntervalSeconds`: Minimum seconds between price OCR attempts while fast mode is active.
- `PriceFastModeSeconds`: How long fast mode stays active after new price data is found.

Good starting point for responsive price reads:

```json
"DefaultIntervalMilliseconds": 250,
"PriceIntervalSeconds": 2,
"ActivePriceIntervalSeconds": 1,
"PriceFastModeSeconds": 30
```

## Full-Hash OCR Cache

- `OcrFullHashCacheEnabled`: When true, every OCR crop is full-hashed before Paddle OCR runs.
- `OcrFullHashCacheMinutes`: How long a cached OCR result stays alive. Reading the same hash refreshes this timer.
- `OcrFullHashCacheMaxEntries`: Maximum OCR hash results kept in memory. Entries store text and hashes, not images.

Good average-PC values:

```json
"OcrFullHashCacheMinutes": 5,
"OcrFullHashCacheMaxEntries": 1000
```

More aggressive cache:

```json
"OcrFullHashCacheMinutes": 10,
"OcrFullHashCacheMaxEntries": 5000
```

## Price Duplicate Protection

- `PriceRecentHashCacheEnabled`: Skips price images already processed recently for the same city.
- `PriceRecentHashCacheMinutes`: How long processed price image hashes stay remembered.
- `PriceRecentHashCacheMaxEntries`: Maximum recent processed price image hashes.

This is separate from the full-hash OCR cache. It prevents saving or processing duplicate price screens too often.

## Price Menu Validation

- `PriceMenuValidationEnabled`: Checks that Buy/Sell menu is visible before reading rows.
- `PriceMenuValidationTopPercent`: Top percent of the price area used for menu validation.
- `PriceMenuValidationUsePreprocess`: Preprocess validation crop before OCR.
- `PriceMenuValidationValidWords`: Words accepted as proof that the menu is open.
- `PriceCaptureBodyOnlyAfterMenuValidation`: After validation, captures only the menu body below the validation area.

If Buy/Sell is missed, recalibrate validation boxes before disabling this.

## Row OCR

- `PriceLayoutValidationPreprocess`: Preprocess Buy/Sell validation layout boxes before OCR.
- `PriceLayoutFieldPreprocess`: Preprocess whole-row crops before OCR.
- `PriceLayoutFieldFallbackEnabled`: If whole-row OCR fails, try separate item/price/multiplier boxes.
- `PriceLayoutRowFingerprintTolerance`: Fuzzy threshold for whole-row fingerprint cache hits. Higher skips more OCR but risks stale row reuse.

Current recommended flow uses whole-row boxes, so keep:

```json
"PriceLayoutFieldFallbackEnabled": false
```

## Coordinate Preprocessing

- `CoordinateTryPreprocess`: Enables coordinate image preprocessing.
- `CoordinateOcrUpscale`: Enlarges the coordinate crop before OCR.
- `CoordinateOcrThreshold`: Brightness cutoff for coordinate text. Higher keeps only brighter pixels; lower keeps more gray/noise.
- `CoordinateForcePreprocess`: Uses preprocessed coordinate OCR first/only when preprocessing is available.
- `OcrPreprocessCleanupEnabled`: Global master switch for binary-image cleanup after preprocessing.
- `CoordinatePreprocessCleanupEnabled`: Enables cleanup for coordinate OCR preprocessing.
- `CoordinatePreprocessRemoveSmallBlobsEnabled`: Removes tiny isolated white particles after thresholding.
- `CoordinatePreprocessMinWhiteBlobPixels`: Minimum connected white-pixel blob size to keep.
- `CoordinatePreprocessTextShapeFilterEnabled`: Removes blobs that are too small/tall/flat to look like OCR text. Default is off because it can be too aggressive.
- `CoordinatePreprocessMinTextLikeBlobWidth`: Minimum blob width when text-shape filtering is enabled.
- `CoordinatePreprocessMinTextLikeBlobHeight`: Minimum blob height when text-shape filtering is enabled.
- `CoordinatePreprocessMaxTextLikeBlobHeightPercent`: Maximum blob height as percent of crop height when text-shape filtering is enabled.

When `SaveDebugImages` is true, coordinate preprocessing also writes stage images under
`Data/debug-ocr/coordinate-preprocess`:

- `before-cleanup`: after upscale + threshold, before blob/text-shape cleanup.
- `after-cleanup`: final image sent to OCR after cleanup.

## Text Presence Gate

- `OcrTextPresenceGateMode`: `Off`, `BeforePreprocess`, `AfterPreprocess`, or `BeforeAndAfter`.
- `OcrTextPresenceMinContrast`: Required brightness contrast.
- `OcrTextPresenceMinEdgePixelsPercent`: Required edge-pixel percent.
- `OcrTextPresenceSampleStep`: Pixel sampling step. Higher is faster but less sensitive.

Faster stable setup:

```json
"OcrTextPresenceGateMode": "BeforePreprocess"
```

Most conservative setup:

```json
"OcrTextPresenceGateMode": "BeforeAndAfter"
```
