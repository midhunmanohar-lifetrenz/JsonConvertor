# JSON Converter Pro 1.0.1

Maintenance release for Windows 10/11 (x64) and macOS 11+ (Apple Silicon and Intel). It fixes the issues found in a full review of 1.0.0.

## Downloads

| File | For |
|---|---|
| `JSONConverterPro-1.0.1-win-x64-setup.exe` | Windows installer (per user, no admin rights) |
| `JSONConverterPro-1.0.1-win-x64-portable.zip` | Windows, no install |
| `JSONConverterPro-1.0.1-macos-arm64.dmg` | Mac with Apple Silicon (M1 and later) |
| `JSONConverterPro-1.0.1-macos-x64.dmg` | Mac with an Intel processor |
| `SHA256SUMS.txt` | Checksums to verify the downloads |

## Fixed in 1.0.1

Data integrity:
- Two copies of the app open at the same time no longer share pasted data. Each copy has its own private temporary folder.
- Linked CSV files whose list names differ only by letter case (`Items`, `items`) no longer overwrite each other.
- A field with an empty name that holds an object no longer overwrites a field of the same name beside it.
- "One row per item" now warns when a record has two or more lists, because every combination of their items is written and totals can be inflated.
- Download is blocked until edited text has been checked again, so it can't export the previous text.

Robustness:
- CSV formula protection no longer changes plain dashes (`-`) or phone-style values (`+91 98765 43210`).
- A damaged character (half of an emoji) is replaced with `�` and reported, instead of stopping the conversion.
- JSON Lines files with very long lines (more than 128 KB each) are recognised.
- Excluding a column no longer blocks the export when a linked list contains only that column.
- A list named `history` gets the sheet name `history_`, because Excel reserves "History".

Other:
- Very large pasted text (more than about 5 MB) goes to the Files page instead of the editor.
- Converting several files asks about replacing existing files once per batch.
- Excel shows milliseconds in date-times and keeps trailing zeros in decimals (`5.0`).
- Lists inside lists of objects keep their grouping when combined into one cell: `[a; b]; c`.
- Controls have names for screen readers.
- Log files older than 30 days are deleted.
- New build for Intel Macs.

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
- The apps are not code-signed yet, so each machine shows a one-time warning:
  - **Windows:** SmartScreen → **More info → Run anyway**.
  - **Mac:** "could not verify" → **Done**, then **System Settings → Privacy & Security → Open Anyway**. Right-click → Open no longer works on macOS 15 and later.
- Hash masking uses a key that changes each time the app restarts, so codes match within a session but not across sessions.

## Verification

- 84 automated tests pass:
  - Engine: flattening, ordering, masking, encodings, JSON Lines, a 10 MB file converted to both formats, and a 100 MB file streamed with a memory check.
  - Headless UI: paste flow, Columns view, presets, files flow, dark theme, privacy cleanup.
- Version 1.0.0 was installed and used on Windows and on Apple Silicon. The 1.0.1 Mac builds were launched on Apple Silicon (the Intel build through Rosetta); 1.0.1 has not yet been run on Windows or on a real Intel Mac.
