# JSON Converter Pro 1.0.0

First release, for Windows 10/11 (x64) and macOS 11+ (Apple Silicon).

## Downloads

| File | For |
|---|---|
| `JSONConverterPro-1.0.0-win-x64-setup.exe` | Windows installer (per user, no admin rights) |
| `JSONConverterPro-1.0.0-win-x64-portable.zip` | Windows, no install |
| `JSONConverterPro-1.0.0-macos-arm64.dmg` | Mac with Apple Silicon |
| `SHA256SUMS.txt` | Checksums to verify the downloads |

## Features

- **Paste-first start screen:** paste JSON, see a live preview, then download Excel or CSV through a Save As dialog.
- **Columns view:** see every detected field (type, fill rate, sample), and include, rename, redact or hash each one.
- **Presets:** save options and column rules for recurring feeds.
- **Files page:** batch conversion with drag-and-drop, per-file validation, and preview with array selection.
- **Input:** JSON, JSON Lines / NDJSON and TXT, in UTF-8, UTF-8 with BOM, or UTF-16. Large files are streamed, not loaded into memory.
- **Structure detection:** finds root arrays, wrapper objects (`status`, `message`, `data`) and nested paths automatically, and asks you to choose when several lists exist.
- **Flattening:** dot-notation columns, four array modes, safe handling of colliding or duplicate names, and nothing silently dropped.
- **Excel output:** typed numbers, booleans and ISO dates, bold frozen header, filters, sensible widths, and automatic extra sheets past the row limit.
- **CSV output:** RFC 4180 quoting, comma, semicolon or tab, UTF-8 with BOM, UTF-8 or UTF-16, formula-injection protection, and fully streamed.
- **Data integrity:** long IDs and leading zeros are preserved, missing and null values are kept distinct, and no partial files are left behind.
- **Privacy:** pasted data is deleted on quit, nothing is uploaded, and there is no telemetry.
- **Other:** settings remembered between runs, recent-conversion history, in-app error log, and light and dark themes.

## Known limits

- Excel output for files of 100 MB or more is best-effort, because Excel workbooks are built in memory. CSV has no such limit.
- Excel cells hold at most 32,767 characters. Longer values are truncated, with a warning.
- The apps are not code-signed yet. Windows SmartScreen and macOS Gatekeeper show a one-time warning, as described in the user guide.
- Hash masking uses a key that changes each time the app restarts, so codes match within a session but not across sessions.

## Verification

- 63 automated tests pass:
  - Engine: flattening, ordering, masking, encodings, JSON Lines, a 10 MB file converted to both formats, and a 100 MB file streamed with a memory check.
  - Headless UI: paste flow, Columns view, presets, files flow, dark theme, privacy cleanup.
- The Windows build passed its initial check on Windows. The macOS build was launched and stayed running on Apple Silicon.
