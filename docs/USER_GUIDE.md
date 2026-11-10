# JSON Converter Pro: user guide

JSON Converter Pro turns JSON data (API responses, HIS / LIS / RIS exports, log files) into Excel workbooks or CSV files.
Everything runs on your computer. Nothing is uploaded, and there is no account or telemetry.

## Install

**Windows 10 / 11 (64-bit)**
- **Installer:** run `JSONConverterPro-1.0.0-win-x64-setup.exe`. It installs for your user only, so no admin rights are needed. Uninstall it from *Settings → Apps*.
- **Portable:** unzip `JSONConverterPro-1.0.0-win-x64-portable.zip` and run `JSONConverterPro.exe`. Nothing is installed.
- If SmartScreen says "Windows protected your PC", choose **More info → Run anyway**. This appears because the app is not code-signed yet.

**macOS 11 or later (Apple Silicon: M1, M2, M3, M4)**
- Open `JSONConverterPro-1.0.0-macos-arm64.dmg` and drag **JSON Converter Pro** into **Applications**.
- The first time, **right-click the app → Open → Open**. After that it opens normally.

You don't need .NET, Excel or Python. Everything the app needs is included.

## Convert pasted JSON (the start screen)

1. **Paste** your JSON into the large text box (Ctrl+V on Windows, ⌘+V on Mac). You can also type, or drop a small `.json` file onto the window.
2. The **live preview** appears underneath as soon as the JSON is valid. The badge shows how many records and columns were found.
   - If the badge is red, the JSON has a mistake. Click **Go to error** to jump to the line.
   - If the JSON contains several lists, choose the one you want under **Data array**.
   - **Format** tidies the JSON so it's easier to read.
3. Click **Download Excel** or **Download CSV**, then choose where to save the file.

Preview tools:
- **Search rows** filters the rows shown in the preview.
- **First 10 / 50 / 200 / 1,000 records** controls how many records the preview loads.

## Choose, rename and hide columns

Open the **Columns** tab next to *Preview*. It lists every field found in the JSON, with its type, how often it's filled, and an example value.

| Option | What it does |
|---|---|
| **Export** checkbox | Untick it to leave the column out of the file. |
| **Rename to** | Gives the column a different header in the output. |
| **Mask → Redact** | Replaces every value with `[REDACTED]`. |
| **Mask → Hash** | Replaces each value with a one-way code. The same value always gives the same code, so you can still count or match rows without seeing the real data. Codes change each time the app is restarted. |

Rules are matched by column name. They apply to the preview, to both download types, to linked sheets, and to files converted on the **Files** page.
Empty and null values stay empty: masking never invents data.

> Masking is a convenience, not a full de-identification process. Review your output against your organisation's data protection policy before sharing it.

## Convert many files at once (Files page)

1. Open **Files** and drag files in, or click **Browse files**. Supported types are `.json`, `.jsonl`, `.ndjson` and `.txt`.
2. Each file is checked straight away. Invalid files show the reason in red and are skipped.
3. Click a file to see it on **File preview**. If a file has several lists, pick one there.
4. Choose **Excel** or **CSV** at the bottom, click **Convert files**, and pick a folder.

If a file with the same name already exists in that folder, you'll be asked before it's replaced.

## Settings

| Setting | What it does |
|---|---|
| **Presets** | Save the current options and column rules under a name, for example "LIS results, masked", and apply them later in one click. |
| **Sheet name** | Name of the Excel worksheet. More sheets are added automatically after 1,048,575 rows. |
| **CSV delimiter / encoding** | Comma, semicolon or tab, and UTF-8 with BOM (best for Excel), UTF-8, or UTF-16. |
| **Array separator** | The text placed between values when a list is combined into one cell (default `; `). |
| **Protect CSV from formula injection** | Adds `'` before text starting with `=`, `+`, `-` or `@`, so Excel can't run it as a formula. Numbers are never changed. |
| **Flatten nested objects** | `{"patient":{"age":35}}` becomes a column named `patient.age`. |
| **Arrays** | Combine into one cell · keep as JSON text · one row per item · separate sheet per list. |
| **Format ISO dates** | `2024-03-15` and `2024-03-15T10:30:00` become real Excel dates. Ambiguous values such as `01/02/2024` stay as text. |
| **Column order** | Smart (ID, Code, Name, Description, Date, Status, Type first), alphabetical, original, or your own list. |
| **Null values** | Leave blank, or write the word `null`. Missing fields always stay blank. |
| **Theme** | Match system, Light or Dark. |

All settings are remembered the next time you open the app.

## How your data is kept safe

- Long IDs (more than 15 digits) are stored as text, so Excel can't round them or show `1.2E+18`. Leading zeros (`000123`) are kept.
- Nothing is silently dropped. Every field becomes a column, and lists of objects keep one column per field.
- If two fields would get the same column name, the second one gets a ` (2)` suffix instead of overwriting the first.
- A cancelled or failed conversion never leaves a half-written file behind.
- Pasted JSON is held in a temporary file only while the app is open, and is deleted when you quit.

## Troubleshooting

| Message | What to do |
|---|---|
| "This file is not valid JSON near line X" | Click **Go to error** (start screen), or open the file in an editor at that line. Check for a missing comma, quote or bracket. |
| "Several data arrays found" | Choose the list to convert under **Data array**. |
| "The file is open in another program" | Close the file in Excel and try again. |
| "There isn't enough free disk space" | Free up space or choose another folder. |
| Very large file is slow in Excel | Use CSV: it streams to disk and has no row limit. |

Details of every warning and error are on the **Error log** page. Technical logs are stored here:
- **Windows:** `%AppData%\JSONConverterPro\logs`
- **Mac:** `~/Library/Application Support/JSONConverterPro/logs`
