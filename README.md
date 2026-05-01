# OCR Trading Backend v4 - Compact Price OCR Fix

This version fixes compact OCR price parsing for values like:

```text
Diamond
2.54068%）
```

The parser now returns:

```text
Diamond price 2540 multiplier 68
```

instead of:

```text
Diamond price 254 multiplier 68
```

## What changed

In `Services/Parsers.cs`, the compact price/multiplier parser now prefers a 2-digit multiplier when the last 3 OCR digits start with `0`.

Examples:

```text
254068 -> price 2540, multiplier 68
432082 -> price 4320, multiplier 82
147076 -> price 1470, multiplier 76
2420123 -> price 2420, multiplier 123
```

Also, trade type detection now accepts `nventory` as Sell because OCR may miss the first `I` in `Inventory`.

## Data files

Edit these files for your game data:

```text
Data/trade-goods.csv
Data/cities.csv
```

This version includes starter entries for:

```text
Gold
Diamond
Pearl
Malachite
```

## Run

```bash
dotnet clean
dotnet restore -r win-x64
dotnet run -c Debug -r win-x64
```

If you changed the database schema from an older version, delete `ocr-trading.db` once before running.
