# JSON Converter Pro

Desktop app that converts JSON, JSON Lines and TXT data into properly structured **Excel (.xlsx)** and **CSV** files.
It runs on **Windows x64** and **macOS** (Apple Silicon and Intel). Users don't need Excel, Python, the .NET SDK or Visual Studio.

- **End users:** read [docs/USER_GUIDE.md](docs/USER_GUIDE.md).
- **What's in this version:** [RELEASE_NOTES.md](RELEASE_NOTES.md).

Built with C# / .NET 8, **Avalonia UI** (one codebase for Windows and macOS), MVVM (CommunityToolkit.Mvvm),
System.Text.Json streaming, ClosedXML, and Microsoft.Extensions DependencyInjection and Logging. All dependencies are open source;
see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Features

- **Paste-first start screen:**
  - A free text area with a live preview: valid/error badge, **Go to error**, **Format**, row search, and a choice of 10 to 1,000 records.
  - **Download Excel** and **Download CSV** open a Save As dialog. No default path is assumed.
- **Columns view:** every detected field with its type, fill rate and a sample, and per-column include, rename and mask (redact or hash).
  Rules apply to the preview, both output formats, linked sheets, and batch conversion.
- **Presets** for recurring feeds (options plus column rules).
- **Files page:** drag-and-drop or Browse, batch conversion, per-file validation and preview, a data-array picker, and a folder chosen for every run.
- **Input:** `.json`, `.jsonl` / `.ndjson`, `.txt`. Encodings: UTF-8, UTF-8 with BOM, UTF-16. JSON Lines are detected automatically.
- **Structure detection:**
  - Root arrays, wrapper objects (`status` / `message` / `data`) and nested paths (`result.items`) are found automatically.
  - When several arrays could be the data, you are asked to choose.
- **Flattening:**
  - Dot notation, with safe handling of colliding or duplicate names.
  - Four array modes: join (one column per field for arrays of objects), JSON text, expand to rows, and separate sheets linked by `_recordNo`.
- **Column order:** Smart (ID, Code, Name, Description, Date, Status, Type, then the rest), Alphabetical, Original or Custom.
- **Excel output:** bold frozen header, filters, capped widths, typed cells, ISO dates, and a new worksheet every 1,048,575 rows.
- **CSV output:** RFC 4180 quoting; comma, semicolon or tab; UTF-8 with BOM, UTF-8 or UTF-16; formula-injection guard; streamed to disk.
- **Also included:** settings remembered between runs, history, an in-app error log plus log files, and light, dark or system theme.

## Data-integrity and privacy rules

| Rule | Behaviour |
|---|---|
| Never discard properties | Every property gets a column. Empty `{}` and `[]` keep a blank column. |
| Missing vs null | Missing → blank. Explicit `null` → blank, or `null` if chosen. Masks never fill empty values. |
| Big IDs | More than 15 significant digits → text in Excel. Integers ≥ 1e11 use number format `0`. |
| Leading zeros | Strings are never converted to numbers. |
| Dates | Only strict ISO-8601 without a time zone becomes an Excel date. |
| Excel vs CSV | Same flattening, ordering and column rules. CSV holds the original text. |
| CSV injection | Text starting with `=` `+` `-` `@` gets a leading `'` (default on). Real numbers are untouched. |
| No overwrite | Save As or a confirmation prompt is shown first. Output is written to a temp file and moved into place at the end. |
| Pasted data | Kept in temp files only while the app runs, and deleted on quit. |

## Build, test, run

```bash
dotnet build JSONConverterPro.sln
dotnet test JSONConverterPro.sln
dotnet run --project JSONConverterPro.App
```

## Release builds

| Command | Output (in `dist/release/` unless noted) |
|---|---|
| `./publish.sh` (macOS) | `.dmg` for Apple Silicon and for Intel Macs, Windows portable `.zip`, `SHA256SUMS.txt`, plus `dist/JSON Converter Pro.app` |
| `./publish.ps1 -Installer` (Windows) | Single-file `JSONConverterPro.exe`, portable `.zip`, and an Inno Setup per-user installer |
| Push tag `v1.0.1` | `.github/workflows/release.yml` builds and smoke-tests everything on GitHub and attaches it to a release |

Every build is self-contained, single-file and Release-configured.
- **macOS:** the native libraries are bundled next to the executable inside the `.app`. The `.app` is ad-hoc signed, not notarized, so users must allow it once in **System Settings → Privacy & Security → Open Anyway**. Notarization needs a **Developer ID Application** certificate from the Apple team's Account Holder or Admin; an *Apple Development* certificate can't be used.
  To sign and notarize, set `CODESIGN_IDENTITY="Developer ID Application: …"` and run `xcrun notarytool`.
- **Windows:** the EXE is unsigned until a code-signing certificate is added.

## Project layout

```
JSONConverterPro.Core/   engine, no UI
  Services/  JsonParserService (streaming) · JsonSchemaService (structure detection) · JsonFlattenService
             ConversionService · PreviewService · ExcelExportService · CsvExportService · FileService
             JsonLinesStream · LoggingService
  Helpers/   ColumnOrderingHelper · ColumnShaper (include/rename/mask) · DateFormatHelper · NumberHelper · FileSizeHelper
JSONConverterPro.App/    Avalonia UI
  ViewModels/  MainViewModel (+ .Paste / .Files / .Tools partials)
  Views/ Models/ Services/ Converters/ Styles/ Resources/
JSONConverterPro.Tests/  xUnit: engine, feature, large-file and headless UI tests
installer/               Inno Setup script for the Windows installer
tools/IconGen/           regenerates AppIcon.png / .ico / .icns
docs/  samples/  publish.sh  publish.ps1  .github/workflows/
```

## Tests

63 automated tests. They cover:
- Every scenario in the original specification.
- Column rules and masking, including linked sheets.
- JSON Lines, including error line numbers.
- CSV encodings and injection protection.
- A **10 MB** file converted to both formats with cell-by-cell equivalence checks.
- A **100 MB** file streamed to CSV with a memory bound.
- Headless UI flows rendered with screenshots in light and dark themes: paste, Columns view, presets, files, privacy cleanup and remembered settings.

## Known limits

- `.xlsx` output for files of 100 MB or more is best-effort, because ClosedXML builds the workbook in memory. CSV is fully streamed.
- Excel cells are limited to 32,767 characters. Longer values are truncated with a warning; CSV keeps them in full.
- `Expand arrays into rows` falls back to joining for a record that would produce more than 5,000 rows, with a warning.
- Hash masking uses a per-session key, so codes match within a session but not across sessions.
