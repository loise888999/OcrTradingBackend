# OCR Trading Companion Backend
# Uncharted Water Online tool

.NET 8 Windows backend for the OCR Trading Companion app.

The backend captures OCR data from the game screen, parses coordinates, cities, and trade-good prices, stores results in SQLite, and exposes APIs used by the React frontend.

## What the backend does

The backend provides:

- OCR control endpoints
- screen capture services
- PaddleOCRSharp OCR integration
- game-window-relative OCR zones
- coordinate OCR parsing
- city OCR parsing
- trade-good price OCR parsing
- compact price/multiplier parsing
- SQLite storage
- trade route search APIs
- trade-good catalog APIs
- pending unknown OCR trade-good review APIs
- CSV import/export for price history

## Main features

### OCR worker

The backend runs an OCR background worker.

It can capture:

- coordinates
- city name
- item/price/trade-type area

OCR can be started or stopped through the frontend or API.

### Game window support

The backend can select and track the game window.

OCR zones are saved relative to the selected game window so the setup remains useful even if the window moves.

### Coordinate OCR

The backend parses coordinate text into:

```text
X
Y
```

Coordinates are stored in SQLite and exposed to the frontend map.

### City OCR

The backend parses city text and matches it to `Data/cities.csv`.

Cities include:

```text
Name
Aliases
MainRegion
SubRegion
SeaTradeRegion
```

### Price OCR

The backend parses trade-good OCR text into:

```text
City
ItemName
TradeGoodType
Price
Multiplier
TradeType
RawText
CapturedAtUtc
```

Trade type can be:

```text
Buy
Sell
Unknown
```

The parser includes fixes for compact OCR price formats.

Example OCR:

```text
Diamond
2.54068%）
```

Expected parsed result:

```text
Diamond price 2540 multiplier 68
```

### Trading APIs

The backend exposes trading APIs for:

- known prices
- city goods
- good locations
- route recommendations
- advanced routes
- multi-good routes
- region filtering

Routes can be filtered by:

- item
- type
- buy regions
- sell regions
- minimum profit
- route count
- multi-good requirements

### Trade-good catalog

The backend loads trade goods from:

```text
Data/trade-goods.csv
```

The catalog supports:

- name
- type
- aliases
- similar-name suggestions
- adding new goods
- saving new goods back to CSV

CSV format:

```csv
Name,Type,Aliases
Diamond,Luxury,Diamoncl|Dlamond
Gold,Mineral,G0ld
```

### Pending unknown OCR goods

When OCR finds a possible trade good that does not match the catalog, the backend can store it in:

```text
Data/pending-trade-goods.json
```

The frontend can then show it in:

```text
Trading Options -> Other
```

The user can:

- accept it as a new trade good
- edit name/type/aliases before accepting
- dismiss it if OCR was wrong

## Requirements

This backend is Windows-specific.

Required:

- Windows 10 or Windows 11
- .NET 8 SDK
- x64 runtime
- Visual Studio 2022 or terminal
- game running in a visible window
- frontend app, optional but recommended

The project uses:

- `net8.0-windows`
- Windows Forms cursor/window APIs
- SQLite via Entity Framework Core
- PaddleOCRSharp
- Paddle Runtime win-x64
- System.Drawing.Common

## Install .NET 8

Check your version:

```bash
dotnet --version
```

If needed, install the .NET 8 SDK from Microsoft.

## Trust HTTPS development certificate

The backend runs on HTTPS by default.

Run once:

```bash
dotnet dev-certs https --trust
```

Then restart your browser.

## Restore and run

From the backend repo root:

```bash
dotnet clean
dotnet restore -r win-x64
dotnet run -c Debug -r win-x64
```

The backend should listen on:

```text
https://localhost:5001
http://localhost:5000
```

For a quick default run after dependencies are restored:

```bash
dotnet run
```

By default, the backend uses the OCR model bundled with PaddleOCRSharp. No extra OCR model download is required for a fresh clone.

## Optional English OCR model

The app can prefer an English PaddleOCR recognition model when the model files are downloaded locally. The files are not committed to git.

Download the optional English OCR files:

```powershell
.\scripts\download-english-ocr-model.ps1
```

This places files under:

```text
Data/ocr-models/english/
```

Then update `appsettings.json`:

```json
"UseEnglishModels": true,
"FallbackToBundledModel": true,
"RecognitionModelPath": "Data/ocr-models/english/rec",
"DictionaryPath": "Data/ocr-models/english/en_dict.txt"
```

`DetectionModelPath` and `ClassifierModelPath` can stay empty. The backend will use the bundled PaddleOCRSharp detector and keep angle classification disabled.

If the English files are missing and `FallbackToBundledModel` is `true`, the backend logs a warning and uses the bundled PaddleOCRSharp model instead.

Health check:

```text
https://localhost:5001/api/health
```

Expected response:

```json
{
  "status": "ok",
  "app": "OCR Trading Backend",
  "timeUtc": "..."
}
```

## Recommended startup order

1. Start the backend:
   ```bash
   dotnet run -c Debug -r win-x64
   ```

2. Open health check:
   ```text
   https://localhost:5001/api/health
   ```

3. Start the frontend:
   ```bash
   npm run dev
   ```

4. Open frontend:
   ```text
   http://localhost:5173
   ```

5. In the frontend, go to Settings and select the game window.

6. Configure OCR zones:
   - Coordinate
   - City
   - Price

7. Start OCR.

## Data files

Important data files:

```text
Data/trade-goods.csv
Data/cities.csv
Data/pending-trade-goods.json
```

## License and third-party notices

This project is licensed under the MIT License. See `LICENSE`.

OCR dependencies and optional English OCR model sources are listed in `THIRD_PARTY_NOTICES.md`.

### `Data/trade-goods.csv`

Used for known trade goods.

Format:

```csv
Name,Type,Aliases
Diamond,Luxury,Diamoncl|Dlamond
Gold,Mineral,G0ld
Pearl,Luxury,PearI
```

Aliases are separated by:

```text
|
```

### `Data/cities.csv`

Used for city matching and region filtering.

Format:

```csv
Name,Aliases,MainRegion,SubRegion,SeaTradeRegion
Alexandria,,Africa,Egypt / Libya,Eastern Mediterranean / Nile
Venice,,Europe,Adriatic,Adriatic Sea
```

### `Data/pending-trade-goods.json`

Stores OCR-detected unknown goods awaiting review.

Usually edited through the frontend, not manually.

## SQLite database

The backend creates a SQLite database automatically.

Common file:

```text
ocr-trading.db
```

If you changed models or schema and the app behaves strangely, stop the backend and delete:

```text
ocr-trading.db
```

Then run again.

The backend will recreate it.

## Main API endpoints

### Health

```http
GET /api/health
```

### OCR control

```http
POST /api/ocr/start
POST /api/ocr/stop
GET  /api/ocr/status
```

### System / game window

```http
GET  /api/system/mouse-position
GET  /api/system/game-window
GET  /api/system/window-under-mouse-delayed
GET  /api/system/select-window-under-mouse-delayed
POST /api/system/clear-selected-game-window
```

### Settings

```http
GET  /api/settings
POST /api/settings/ocr-zone
POST /api/settings/value
```

### OCR results

```http
GET /api/coordinates/latest
GET /api/cities/latest
GET /api/prices/history
```

### Catalogs

```http
GET  /api/cities
GET  /api/trade-goods
GET  /api/trade-goods/suggestions
POST /api/trade-goods
```

### Pending unknown trade goods

```http
GET  /api/pending-trade-goods
POST /api/pending-trade-goods/{id}/accept
POST /api/pending-trade-goods/{id}/dismiss
```

### Regions

```http
GET /api/regions/main
GET /api/regions/sub
GET /api/regions/sea-trade
```

### Trading

```http
GET /api/trading/search
GET /api/trading/city-goods
GET /api/trading/good-locations
GET /api/trading/recommendations
GET /api/trading/good-lookup
GET /api/trading/known-prices
GET /api/trading/advanced-routes
GET /api/trading/multi-good-routes
```

### CSV import/export

```http
GET  /api/export/prices.csv
POST /api/import/prices.csv
```

## Adding a trade good manually

Request:

```http
POST /api/trade-goods
Content-Type: application/json
```

Body:

```json
{
  "name": "Diamond",
  "type": "Luxury",
  "aliases": ["Diamoncl", "Dlamond"]
}
```

The backend writes it into:

```text
Data/trade-goods.csv
```

## Accepting a pending OCR trade good

Request:

```http
POST /api/pending-trade-goods/{id}/accept
Content-Type: application/json
```

Body:

```json
{
  "name": "Diamond",
  "type": "Luxury",
  "aliases": ["Diamoncl", "Dlamond"],
  "force": true
}
```

This adds it to the trade-good CSV and marks the pending candidate as accepted.

## Dismissing a pending OCR trade good

```http
POST /api/pending-trade-goods/{id}/dismiss
```

Use this when OCR detected something that is not a real trade good.

## CORS

The backend allows these frontend origins by default:

```text
http://localhost:5173
http://localhost:5174
http://localhost:3000
```

If the frontend runs on another port, update the CORS policy in `Program.cs`.

## Troubleshooting

### Frontend says backend is offline

Check:

```text
https://localhost:5001/api/health
```

If the certificate warning appears, run:

```bash
dotnet dev-certs https --trust
```

### OCR does not detect anything

Check:

- OCR is started
- the correct game window is selected
- OCR zones are configured
- the game window is visible
- the OCR text is inside the selected zone
- Windows scaling is not causing wrong coordinates

### Wrong OCR zone after moving the game window

Re-select the game window in the frontend settings and save the OCR zones again.

### Trade good already exists

The backend checks both names and aliases.

If you try to add a duplicate, it will return an error.

### New trade good does not show in frontend

Refresh the frontend catalog or reload the page.

### Bad OCR item keeps appearing

Dismiss it in:

```text
Trading Options -> Other -> OCR-detected unknown goods
```

If it still appears later, add a better alias to the correct trade good.

## Development notes

Useful commands:

```bash
dotnet clean
dotnet restore -r win-x64
dotnet build -c Debug -r win-x64
dotnet run -c Debug -r win-x64
```

## Future improvement ideas

Good next backend improvements:

- add city X/Y coordinates for real distance calculations
- add route distance or sailing time estimates
- add API for editing existing trade goods
- add API for deleting trade goods
- add API for importing/exporting trade-good catalog
- add price freshness filters server-side
- add confidence score to parsed prices
- add OCR screenshot debug endpoint
- add backup creation before editing CSV files
- add migrations instead of `EnsureCreated`
