# JSON Converter Pro

Desktop utility that converts JSON / TXT files into properly structured **Excel (.xlsx)** and **CSV** files.
Runs on **Windows x64** and **macOS Apple Silicon**. No Excel, Python, .NET SDK or Visual Studio needed to run it.

> Built with C# / .NET 8 and **Avalonia UI** (WPF is Windows-only, so Avalonia is used to get one codebase on both platforms).
> MVVM · System.Text.Json (streaming) · ClosedXML · Microsoft.Extensions.DependencyInjection / Logging · all free, open-source.

## Features

- **Paste-first home screen**: paste or type JSON into a free text area, see the live preview (first 10 records, valid/error badge, array picker) on the same screen, then **Download Excel** or **Download CSV**. A Save As dialog lets you choose where the file goes (no default path is assumed). Ctrl+V / ⌘+V works anywhere; dropping a small file on the screen fills the text area
- Files page: drag-and-drop or Browse, multiple files, per-file size/status, remove file, batch conversion
- Automatic structure detection: root array, root object, wrapper objects (`status` / `message` / `data`), nested paths (`result.items`).
  If several arrays of objects exist you must pick one (no guessing). "Whole object as one record" is always offered for wrappers.
- Flattening with dot notation (`demographics.age`), duplicate / colliding names kept safely (`a.b` vs `{"a":{"b"}}` → `a.b`, `a.b (2)`)
- Arrays: join with `;` (arrays of objects keep **one column per field**, aligned by index) · compact JSON text · expand to rows · separate sheets/CSV files linked by `_recordNo`
- Column order: Smart (ID, Code, Name, Description, Date, Status, Type, rest) · Alphabetical · Original · Custom
- Preview of the first 10 records with detected columns, total records/columns, horizontal + vertical scroll; cleared when the selected file changes
- Excel: bold frozen header, filters, capped auto widths, typed numbers/booleans/dates, Unicode, automatic extra worksheet every 1,048,575 data rows
- CSV: UTF-8 with BOM, RFC 4180 quoting (commas, quotes, line breaks), comma / semicolon / tab, fully streamed to disk
- Progress with record count, cancel, overwrite confirmation, last output folder remembered, recent-conversion history, in-app error log (+ log files)

## Data-integrity rules implemented

| Rule | Behaviour |
|---|---|
| Never discard properties | Every property gets a column; empty `{}` / `[]` keep a (blank) column; arrays of objects are never reduced to a single field |
| Missing vs null | Missing → blank. Explicit `null` → blank, or the word `null` if *Null values → write null* is chosen |
| Big IDs | Numbers with more than 15 significant digits are written as **text** in Excel so no digits are lost; integers ≥ 1e11 use format `0` (no `1.2E+11`) |
| Leading zeros | Strings are never converted to numbers (`"000123"` stays `000123`) |
| Dates | Only strict ISO-8601 without a time-zone (`2024-03-15`, `2024-03-15T10:30:00`) become Excel dates. `01/02/2024`, `20240315`, and values with `Z`/offset stay text |
| Excel vs CSV | Same flattening and ordering code; CSV holds the original text of every value |
| No overwrite | Existing output → confirmation prompt; output is written to a temp file and moved into place, so a cancel/failure never leaves a partial file |

## Build and run

Requires the .NET 8 SDK for development only.

```bash
dotnet build JSONConverterPro.sln
dotnet run --project JSONConverterPro.App
dotnet test JSONConverterPro.sln
```

## Publish (self-contained, single file, Release)

```bash
./publish.sh          # macOS arm64 (.app + .dmg) and Windows x64 (.exe) -> ./dist
./publish.sh mac
./publish.sh win
```

```powershell
./publish.ps1         # Windows x64 -> dist\win-x64\JSONConverterPro.exe
```

Equivalent manual command:

```powershell
dotnet publish JSONConverterPro.App/JSONConverterPro.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Outputs: `dist/win-x64/JSONConverterPro.exe`, `dist/JSON Converter Pro.app`, `dist/JSONConverterPro-1.0.0-macos-arm64.dmg`.

### macOS notes
- On macOS the native libraries (`libAvaloniaNative`, `libSkiaSharp`, `libHarfBuzzSharp`) are not embedded in the single file; `publish.sh` copies them into the `.app` next to the executable.
- The app is **ad-hoc signed** (required for arm64 to run) but not notarized. First launch on another Mac: right-click → Open. For wide distribution sign with a Developer ID and notarize: `CODESIGN_IDENTITY="Developer ID Application: …" ./publish.sh mac`, then `xcrun notarytool submit …`.

### Windows notes
- If SmartScreen warns about an unsigned EXE, choose *More info → Run anyway*, or sign the EXE with a code-signing certificate.

## Project layout

```
JSONConverterPro.sln
JSONConverterPro.Core/      engine, no UI: Models, Services, Helpers
  Services/  JsonParserService (streaming) · JsonSchemaService (structure detection) · JsonFlattenService
             ExcelExportService · CsvExportService · ConversionService · PreviewService · FileService · LoggingService
  Helpers/   FileSizeHelper · DateFormatHelper · NumberHelper · ColumnOrderingHelper
JSONConverterPro.App/       Avalonia UI: Views, ViewModels/MainViewModel, Models, Services, Converters, Styles, Resources
JSONConverterPro.Tests/     xUnit: engine tests + headless UI tests (Avalonia.Headless)
tools/IconGen/              regenerates AppIcon.png / .ico / .icns (dotnet run --project tools/IconGen -- <outDir>)
samples/                    sample JSON files (nested HIS/LIS style, multiple arrays, unicode, collisions, invalid, empty)
publish.sh  publish.ps1
```

The engine lives in a separate `Core` library so it is fully testable and identical on both platforms.

## Tests

46 automated tests, including: simple/nested/array-of-objects, null and missing, empty and invalid JSON, Unicode (UTF-8, UTF-8 BOM, UTF-16),
duplicate/colliding columns, big IDs and leading zeros, ISO date detection, column ordering modes, CSV escaping and delimiters,
overwrite confirmation, cancellation (no files left behind), multiple independent files, preview isolation between files,
Excel row-limit splitting, a generated **10 MB** file converted to both formats with cell-by-cell equivalence checks,
a **100 MB** file streamed to CSV with a bounded memory check, a record larger than the read buffer, and end-to-end UI flows rendered headlessly.

## Known limits

- Excel output is built in memory by ClosedXML, so 100 MB+ files are best-effort for `.xlsx`; **CSV is fully streamed** and has no row limit.
- Excel cells are limited to 32,767 characters; longer values are truncated with a warning (CSV keeps them in full).
- `Expand arrays into rows` multiplies rows when several arrays are expanded in one record; above 5,000 rows for a record the arrays are joined instead, with a warning.
- Newline-delimited JSON (one object per line) is not supported; wrap the lines in an array.
- Opening a CSV in Excel will still show 15+ digit numbers in scientific notation (Excel behaviour). Use `.xlsx` for long IDs.
