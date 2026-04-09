# PhotoOrganizer

A Windows desktop application that automatically organizes photos and videos into a clean, date-based folder structure. Built with .NET 8, Dear ImGui (via OpenGL), and ImageMagick.

![Platform](https://img.shields.io/badge/platform-Windows-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple) ![License](https://img.shields.io/badge/license-Non--Commercial-orange)

---

## What It Does

PhotoOrganizer takes a messy source folder full of photos and videos — including Google Photos Takeout exports — and sorts them into a structured output hierarchy like:

```
2023/
  06/
    2023-06-15_14-22-08.png
    2023-06-15_14-23-01.mp4
  08/
    2023-08-03_09-11-47.png
_unknown_date/
  undated_pic.jpg
```

Every file is renamed to its capture timestamp, converted to a consistent format (PNG for images, MP4 for video), and verified for integrity before the source is moved to a "Processed" folder.

---

## Features

- **Date-based organization** — resolves the capture date from EXIF, Google Takeout JSON sidecars, or file modification time, with a clear fallback chain
- **Missing date inference** — estimates dates for undated files by interpolating between dated neighbours in the same folder
- **Google Photos Takeout support** — automatically finds and parses `.json` sidecar files, including Google's duplicate-naming quirks
- **HEIC/HEIF conversion** — converts Apple HEIC images to PNG with full EXIF/GPS preservation
- **Video conversion** — re-encodes to H.264 MP4 via FFmpeg with embedded `creation_time` metadata
- **EXIF & GPS preservation** — extracts raw EXIF bytes and injects them intact into output files; handles GPS coordinates as degree/minute/second rationals
- **MD5 integrity verification** — every copy and conversion is verified; failures are isolated to a "Failed" folder without stopping the run
- **Parallel processing** — configurable thread count, adjustable live during a run
- **Skip already-processed files** — SQLite history database tracks completed files across sessions
- **Collision-safe naming** — appends `_0001`, `_0002`, etc. when filenames collide, even across parallel threads
- **EXIF Viewer panel** — inspect all metadata tags for any photo in a folder
- **Verification panel** — scan folder trees for extension breakdown, orphan JSON files, and structural issues
- **Pause / Resume / Stop** — full control over long-running operations

---

## Requirements

- **Windows 10 / 11** (64-bit)
- **.NET 8 runtime**
- **OpenGL 4.0+** compatible GPU
- **FFmpeg on PATH** — optional; required only for video conversion (`ffmpeg -version` must work)

---

## Supported Formats

| Category | Input | Output |
|----------|-------|--------|
| Images | `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff` | `.png` |
| HEIC | `.heic`, `.heif` | `.png` |
| Video | `.mov`, `.mp4`, `.avi`, `.mkv`, `.3gp` | `.mp4` |

---

## How It Works

### Processing Pipeline

For each file in the source folder (optionally recursive):

1. **Classify** by file extension → Image / HEIC / Video / Unknown
2. **Resolve date** using this priority chain:
   - EXIF `DateTimeOriginal` (tag 36867)
   - EXIF `DateTimeDigitized` (tag 36868)
   - Google Takeout JSON sidecar `photoTakenTime.timestamp`
   - File last-write time (flagged as "unknown date")
3. **Infer missing dates** (if enabled) — scan folder peers, interpolate midpoint between the nearest dated neighbours
4. **Compute output path**:
   - Known date → `{export}/{year}/{month}/{timestamp}.{ext}` or `{export}/{year}/{timestamp}.{ext}`
   - Unknown date → `{export}/_unknown_date/{original_name}`
5. **Convert / copy**:
   - Images/HEIC → Magick.NET PNG conversion with EXIF injection
   - Videos → FFMpeg H.264/AAC re-encode with metadata embedding
   - Straight copies → verified by MD5 hash
6. **Verify** output integrity (hash match, dimensions, FFProbe analysis)
7. **Move source** to the Processed folder (preserving subfolder structure), including any JSON sidecar
8. On failure → move source to the Failed folder and log the error; the run continues

### Date Inference

When a file has no resolvable date and inference is enabled, the app looks at all files in the same folder, sorts them by filename, and assigns the midpoint timestamp between the nearest dated files before and after the unknown one. Inferred dates are shown in the log as `~ est. date`.

### Metadata Preservation

EXIF data is handled at the byte level — raw TIFF bytes are extracted from the source and injected into the output PNG as an `eXIf` chunk, updating only the date and GPS tags. This avoids lossy metadata re-encoding.

---

## Configuration

Settings are saved to `%APPDATA%/PhotoOrganizer/settings.json` and loaded on startup. All options are configured through the UI:

| Setting | Description |
|---------|-------------|
| Source folder | Where to read files from |
| Export folder | Where organized output goes |
| Processed folder | Where successfully processed sources are moved |
| Failed folder | Where failed sources are isolated |
| Folder structure | Year + Month (default) or Year only |
| Include subfolders | Recurse into subdirectories |
| Infer missing dates | Estimate dates from neighbours |
| Skip processed files | Skip files already in the history database |
| Thread count | 1 to CPU core count, adjustable during a run |

Processing history is stored in `%APPDATA%/PhotoOrganizer/history.db` (SQLite).

---

## Tech Stack

| Component | Library |
|-----------|---------|
| UI | [Dear ImGui](https://github.com/ocornut/imgui) via [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) + [OpenTK](https://opentk.net/) |
| Image processing | [Magick.NET](https://github.com/dlemstra/Magick.NET) (Q16) |
| Video conversion | [FFMpegCore](https://github.com/rosenbjerg/FFMpegCore) |
| JSON parsing | [Newtonsoft.Json](https://www.newtonsoft.com/json) |
| Database | [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) |
| Platform | .NET 8, C# 12, Windows Forms (splash only) |

---

## License

Copyright © 2026 Simon Rozner. Non-commercial use only with attribution required. See [LICENSE](LICENSE) for full terms. Commercial licensing available on request.
