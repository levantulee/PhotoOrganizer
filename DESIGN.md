# PhotoOrganizer — Design Document

## Overview

PhotoOrganizer is a Windows desktop tool that organizes photos and videos from a source folder into a clean date-based folder structure. It handles Google Photos Takeout exports (including JSON sidecar metadata), HEIC images, video conversion to MP4, EXIF preservation, and data integrity verification — all via an ImGui.NET desktop UI.

---

## Architecture

### Project

- **Framework:** .NET 8, `net8.0-windows`, SDK-style `.csproj`
- **UI:** ImGui.NET (Dear ImGui) rendered via OpenTK (OpenGL)

### NuGet Packages

| Package | Purpose |
|---|---|
| `ImGui.NET` | Dear ImGui bindings |
| `OpenTK` | OpenGL window + input for ImGui rendering backend |
| `Magick.NET-Q16-AnyCPU` | HEIC decode, image processing, EXIF read/write |
| `FFMpegCore` | Video conversion to MP4 (wraps ffmpeg.exe) |
| `Newtonsoft.Json` | Google Takeout JSON sidecar parsing |

> **FFmpeg dependency:** `FFMpegCore` requires `ffmpeg.exe` on PATH. The UI shows an error at startup if FFmpeg is not found.

---

## File Structure

```
PhotoOrganizer/
├── Program.cs                   # Entry point — creates AppWindow, calls Run()
├── UI/
│   └── AppWindow.cs             # OpenTK window + ImGui render loop, all UI state
├── Core/
│   ├── Processor.cs             # Pipeline orchestrator, fires progress events
│   ├── MetadataService.cs       # EXIF + JSON sidecar reading, date resolution
│   ├── FileOrganizer.cs         # Output path/name computation, collision handling
│   ├── VideoConverter.cs        # FFMpegCore MP4 conversion
│   └── IntegrityChecker.cs      # MD5 hash before/after, validation
├── Models/
│   ├── FileEntry.cs             # One source file + its resolved metadata
│   ├── ProcessResult.cs         # Per-file outcome (success/failed/skipped + reason)
│   └── RunStats.cs              # Aggregate counters for the UI stats bar
├── DESIGN.md                    # This document
└── PhotoOrganizer.csproj        # SDK-style, net8.0-windows
```

---

## UI Layout (ImGui)

```
┌─ PhotoOrganizer ──────────────────────────────────────┐
│ Source Folder:    [C:\Photos________________] [Browse] │
│ Export Folder:    [D:\Sorted________________] [Browse] │
│ Processed Folder: [C:\Photos\Done___________] [Browse] │
│ Failed Folder:    [C:\Photos\Failed_________] [Browse] │
│                                                        │
│ Folder structure:  (•) Year + Month   ( ) Year only   │
│                                                        │
│  [  Start  ]   [  Stop  ]                              │
│                                                        │
│ Photos: 142  Videos: 23  HEIC: 18  Skipped: 4  Err: 2│
│ ────────────────────────────────────────────────────── │
│ [LOG - scrollable, auto-scroll to bottom]              │
│  ✓ 2023-06-15_14-22-08.png  ←  IMG_1234.JPG           │
│  ✓ 2023-06-15_14-23-01.mp4  ←  VID_5678.MOV (conv)   │
│  ⚠ no date: screenshot.png  →  _unknown_date/         │
│  ✗ corrupt.jpg — Parameter is not valid                │
└────────────────────────────────────────────────────────┘
```

`AppWindow.cs` owns all ImGui state (paths, running flag, log lines, stats). It holds a reference to `Processor` and subscribes to its progress event to append log lines and update counters on the UI thread.

---

## Processing Pipeline (`Processor.cs`)

For each file discovered in the source folder (recursive):

1. **Classify** by extension → image / HEIC / video / unsupported
2. **Resolve date** via `MetadataService` (see priority chain below)
3. **Compute output path** via `FileOrganizer`
4. **Convert/copy:**
   - HEIC → PNG via `MagickImage` (Magick.NET)
   - Video → MP4 via `FFMpegCore`
   - JPG/PNG → converted to PNG (all output is PNG for images)
5. **Apply metadata** to output file via Magick.NET — writes `DateTimeOriginal` + GPS tags back into the PNG EXIF
6. **Verify integrity** via `IntegrityChecker`
7. **Move source** to Processed folder (success) or Failed folder (failure)
8. **Fire progress event** → UI appends log line, updates stats

Processing runs on a background `Task` so the UI stays responsive. A `CancellationToken` lets Stop work cleanly.

---

## Metadata Resolution (`MetadataService.cs`)

### Date priority chain (highest → lowest)

1. EXIF tag 36867 — `DateTimeOriginal`  
   Parse with exact format `"yyyy:MM:dd HH:mm:ss"`, trim null terminator
2. EXIF tag 36868 — `DateTimeDigitized`
3. Google Takeout JSON sidecar — `photoTakenTime.timestamp` (Unix epoch → local time)
4. `File.GetLastWriteTime()` — last resort; these files go to `_unknown_date/` subfolder

### JSON sidecar lookup

For a file `IMG_1234.JPG`, look for:
1. `IMG_1234.JPG.json` (exact Google Takeout format)
2. `IMG_1234.json` (extension-stripped)
3. Prefix match (Google Takeout truncates long filenames in the JSON name)

### GPS / geodata

GPS is sourced from (in priority order):
1. EXIF GPS tags in the original file
2. `geoData` block in the Google Takeout JSON (`latitude`, `longitude`, `altitude`)

All GPS data is written back into the output file's EXIF via Magick.NET's `ExifProfile`.

---

## Output Naming & Folder Structure (`FileOrganizer.cs`)

### Folder structure modes (radio button in UI)

| Mode | Example path |
|---|---|
| Year + Month (default) | `2023\06\2023-06-15_14-22-08.png` |
| Year only | `2023\2023-06-15_14-22-08.png` |

- All folder names are **fully numeric** (no month name abbreviations)
- Month folder is always zero-padded (`06`, not `6`)

### Filename format

`yyyy-MM-dd_HH-mm-ss` + original extension replaced with `.png` for images  
Example: `2023-06-15_14-22-08.png`

### Collision handling

If the target filename already exists, append a counter: `_2`, `_3`, etc.  
Example: `2023-06-15_14-22-08_2.png`

### Unknown date

Files with no resolvable date → `<export_root>\_unknown_date\` with original filename preserved.

---

## Video Conversion (`VideoConverter.cs`)

Supported input formats: `.mov`, `.mp4`, `.avi`, `.mkv`, `.3gp`  
Output: MP4 (H.264 video, AAC audio)

```csharp
await FFMpegArguments
    .FromFileInput(sourceFile)
    .OutputToFile(outputFile, true, options => options
        .WithVideoCodec(VideoCodec.LibX264)
        .WithAudioCodec(AudioCodec.Aac)
        .WithFastStart())
    .ProcessAsynchronously();
```

Date metadata is embedded via ffmpeg `-metadata creation_time=<ISO8601>`.

**FFmpeg not found:** Check at startup. If missing, show in UI: *"FFmpeg not found — video conversion disabled. Install FFmpeg and add to PATH."* Images will still be processed normally.

---

## Integrity Checking (`IntegrityChecker.cs`)

| Scenario | Check method |
|---|---|
| PNG copy (no conversion) | MD5 of source bytes == MD5 of output bytes |
| HEIC → PNG conversion | Output readable by Magick.NET + non-zero pixel dimensions |
| Video → MP4 conversion | FFProbe can read the output without error |

For conversions, the source MD5 is logged alongside the output path for audit purposes. A hash mismatch on a straight copy is a hard failure — source file goes to Failed folder.

---

## Source File Movement

| Outcome | Action |
|---|---|
| Success | `File.Move` → Processed folder, preserving relative subdirectory structure |
| Failure | `File.Move` → Failed folder, flat (no subdirs), error logged |

If the move itself fails (locked file, permissions error), log the error and continue — do not crash the run.

---

## Supported File Types

| Category | Extensions |
|---|---|
| Images (→ PNG) | `.jpg`, `.jpeg`, `.png` |
| HEIC (→ PNG) | `.heic`, `.heif` |
| Videos (→ MP4) | `.mov`, `.mp4`, `.avi`, `.mkv`, `.3gp` |
| Unsupported | Logged and skipped |

Extension matching is case-insensitive.

---

## Verification Checklist

1. Run with a test folder containing: JPG with EXIF, JPG without EXIF + companion `.json`, HEIC, PNG, MP4, MOV, corrupt file
2. Verify output has correct `YYYY/MM/yyyy-MM-dd_HH-mm-ss.png` structure
3. Verify EXIF DateTimeOriginal is present in output PNGs (Windows Photos → file info)
4. Verify GPS coordinates are present in output if source had geodata
5. Verify converted MP4 plays and has correct `creation_time` metadata
6. Verify source files moved to Processed folder
7. Verify corrupt file appears in Failed folder with log entry
8. Verify UI log and stats match actual file counts
9. Test Stop button mid-run — verify no partial/corrupt output files remain
