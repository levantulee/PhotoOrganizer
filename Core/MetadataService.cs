using ImageMagick;
using Newtonsoft.Json.Linq;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class MetadataService
{
    /// <summary>
    /// Resolves metadata for <paramref name="filePath"/>.
    /// When <paramref name="useDateFallback"/> is <c>true</c>, files with no EXIF/sidecar date
    /// get their date from the older of the filesystem created/modified timestamps instead of
    /// being marked as unknown. The JSON sidecar timestamp is also compared when present.
    /// </summary>
    public FileEntry Resolve(string filePath, bool useDateFallback = false)
    {
        var entry = new FileEntry
        {
            SourcePath = filePath,
            Category = ClassifyFile(filePath)
        };

        if (entry.Category == FileCategory.Unknown)
            return entry;

        // For videos, try JSON sidecar only (can't read EXIF directly here without ffprobe)
        if (entry.Category == FileCategory.Video)
        {
            TryReadJsonSidecar(entry);
            if (entry.ResolvedDate == null)
            {
                if (useDateFallback)
                    ApplyFilesystemFallback(entry, filePath);
                else
                {
                    entry.ResolvedDate = File.GetLastWriteTime(filePath);
                    entry.DateIsUnknown = true;
                }
            }
            return entry;
        }

        // Vector/non-raster PassThrough formats (SVG, AI, EPS, WMF) don't carry EXIF
        // and rendering them via Magick just to find nothing would be slow.
        var srcExtLower = Path.GetExtension(filePath).ToLowerInvariant();
        if (entry.Category == FileCategory.PassThrough &&
            srcExtLower is ".svg" or ".ai" or ".eps" or ".wmf")
        {
            TryReadJsonSidecar(entry);
            if (entry.ResolvedDate == null)
            {
                if (useDateFallback)
                    ApplyFilesystemFallback(entry, filePath);
                else
                {
                    entry.ResolvedDate = File.GetLastWriteTime(filePath);
                    entry.DateIsUnknown = true;
                }
            }
            return entry;
        }

        // Images, HEIC, RAW, and GIF: try EXIF first
        try
        {
            using var image = new MagickImage(filePath);
            var exif = image.GetExifProfile();

            if (exif != null)
            {
                // Try DateTimeOriginal (36867)
                var dto = exif.GetValue(ExifTag.DateTimeOriginal);
                if (dto != null)
                {
                    var parsed = ParseExifDate(dto.Value?.ToString());
                    if (parsed.HasValue)
                    {
                        entry.ResolvedDate = parsed;
                    }
                }

                // Try DateTimeDigitized (36868)
                if (entry.ResolvedDate == null)
                {
                    var dtd = exif.GetValue(ExifTag.DateTimeDigitized);
                    if (dtd != null)
                    {
                        var parsed = ParseExifDate(dtd.Value?.ToString());
                        if (parsed.HasValue)
                        {
                            entry.ResolvedDate = parsed;
                        }
                    }
                }

                // Read GPS from EXIF
                ReadGpsFromExif(exif, entry);
            }
        }
        catch
        {
            // EXIF read failed — fall through to JSON
        }

        // Try JSON sidecar
        if (entry.ResolvedDate == null || entry.GpsLatitude == null)
        {
            TryReadJsonSidecar(entry);
        }

        // Final fallback
        if (entry.ResolvedDate == null)
        {
            if (useDateFallback)
                ApplyFilesystemFallback(entry, filePath);
            else
            {
                entry.ResolvedDate = File.GetLastWriteTime(filePath);
                entry.DateIsUnknown = true;
            }
        }

        return entry;
    }

    /// <summary>
    /// Sets <see cref="FileEntry.ResolvedDate"/> to the oldest available timestamp:
    /// the minimum of filesystem created time, filesystem modified time, and any
    /// date already found in a JSON sidecar. Marks the entry as <see cref="FileEntry.DateFromFilesystem"/>.
    /// </summary>
    private static void ApplyFilesystemFallback(FileEntry entry, string filePath)
    {
        var candidates = new List<DateTime>
        {
            File.GetCreationTime(filePath),
            File.GetLastWriteTime(filePath)
        };

        // If a sidecar was read but returned no photoTakenTime, its GPS may still be set —
        // that's fine. If it DID set a date, entry.ResolvedDate is already non-null and we
        // never reach here. So here we can safely add it only if it wasn't already consumed.
        // (No extra action needed — sidecar date is handled before we reach this method.)

        var oldest = candidates.Where(d => d != default && d.Year > 1970).DefaultIfEmpty().Min();
        if (oldest == default)
            oldest = File.GetLastWriteTime(filePath); // absolute last resort

        entry.ResolvedDate     = oldest;
        entry.DateFromFilesystem = true;
        entry.DateIsUnknown    = false;
    }

    /// <summary>
    /// For every entry whose date is unknown, looks at the nearest files (by filename sort order)
    /// in the same directory that DO have a date, then interpolates. Files bracketed between two
    /// dated neighbours get the midpoint; files at the start/end of a sequence get the neighbour's date.
    /// Entries that cannot be bracketed at all (the whole directory has no dates) stay unknown.
    /// </summary>
    public static void InferMissingDates(List<FileEntry> entries)
    {
        var byDir = entries
            .GroupBy(e => Path.GetDirectoryName(e.SourcePath) ?? "",
                     StringComparer.OrdinalIgnoreCase);

        foreach (var group in byDir)
        {
            var sorted = group
                .OrderBy(e => Path.GetFileName(e.SourcePath), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!sorted.Any(e => !e.DateIsUnknown && e.ResolvedDate.HasValue))
                continue; // whole directory is undated — nothing to infer from

            for (int i = 0; i < sorted.Count; i++)
            {
                var entry = sorted[i];
                if (!entry.DateIsUnknown) continue;

                // Nearest dated file before this one (by filename sort)
                DateTime? before = null;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (!sorted[j].DateIsUnknown && sorted[j].ResolvedDate.HasValue)
                    { before = sorted[j].ResolvedDate; break; }
                }

                // Nearest dated file after this one
                DateTime? after = null;
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (!sorted[j].DateIsUnknown && sorted[j].ResolvedDate.HasValue)
                    { after = sorted[j].ResolvedDate; break; }
                }

                if (!before.HasValue && !after.HasValue) continue;

                entry.ResolvedDate = (before.HasValue && after.HasValue)
                    ? new DateTime((before.Value.Ticks + after.Value.Ticks) / 2) // midpoint
                    : (before ?? after)!.Value;                                   // nearest

                entry.DateIsUnknown   = false;
                entry.DateIsEstimated = true;
            }
        }
    }

    public static FileCategory ClassifyFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or
            ".webp" or ".bmp" or ".avif" => FileCategory.Image,
            ".heic" or ".heif" => FileCategory.Heic,
            ".mov" or ".mp4" or ".avi" or ".mkv" or ".3gp" or
            ".wmv" or ".m4v" or ".mts" or ".m2ts" or ".webm" or ".flv" or ".ts" => FileCategory.Video,
            ".raw" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" or
            ".orf" or ".rw2" or ".pef" or ".srw" or ".raf" or ".3fr" or
            ".psd" or ".xcf" => FileCategory.Raw,
            ".gif" or ".svg" or ".ai" or ".eps" or ".wmf" => FileCategory.PassThrough,
            _ => FileCategory.Unknown
        };
    }

    private static DateTime? ParseExifDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.TrimEnd('\0').Trim();
        if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt;
        }
        return null;
    }

    private static void ReadGpsFromExif(IExifProfile exif, FileEntry entry)
    {
        try
        {
            var latRef = exif.GetValue(ExifTag.GPSLatitudeRef)?.Value?.ToString();
            var latVal = exif.GetValue(ExifTag.GPSLatitude)?.Value;
            var lonRef = exif.GetValue(ExifTag.GPSLongitudeRef)?.Value?.ToString();
            var lonVal = exif.GetValue(ExifTag.GPSLongitude)?.Value;

            if (latVal is Rational[] latRationals && lonVal is Rational[] lonRationals)
            {
                double lat = RationalsToDegrees(latRationals);
                double lon = RationalsToDegrees(lonRationals);

                if (latRef == "S") lat = -lat;
                if (lonRef == "W") lon = -lon;

                if (lat != 0 || lon != 0)
                {
                    entry.GpsLatitude = lat;
                    entry.GpsLongitude = lon;

                    var altVal = exif.GetValue(ExifTag.GPSAltitude)?.Value;
                    if (altVal is Rational altRational && altRational.Denominator != 0)
                    {
                        double alt = (double)altRational.Numerator / altRational.Denominator;
                        var altRef = exif.GetValue(ExifTag.GPSAltitudeRef)?.Value;
                        if (altRef is byte b && b == 1) alt = -alt;
                        entry.GpsAltitude = alt;
                    }
                }
            }
        }
        catch { /* GPS read failure is non-fatal */ }
    }

    private static double RationalsToDegrees(Rational[] rationals)
    {
        if (rationals.Length < 3) return 0;
        double deg = rationals[0].Denominator != 0 ? (double)rationals[0].Numerator / rationals[0].Denominator : 0;
        double min = rationals[1].Denominator != 0 ? (double)rationals[1].Numerator / rationals[1].Denominator : 0;
        double sec = rationals[2].Denominator != 0 ? (double)rationals[2].Numerator / rationals[2].Denominator : 0;
        return deg + min / 60.0 + sec / 3600.0;
    }

    private void TryReadJsonSidecar(FileEntry entry)
    {
        var sidecar = FindJsonSidecar(entry.SourcePath);
        if (sidecar == null) return;

        entry.JsonSidecarPath = sidecar;

        try
        {
            var json = JObject.Parse(File.ReadAllText(sidecar));

            // Date from photoTakenTime
            if (entry.ResolvedDate == null)
            {
                var ts = json["photoTakenTime"]?["timestamp"]?.Value<string>();
                if (ts != null && long.TryParse(ts, out long epoch))
                {
                    entry.ResolvedDate = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
                }
            }

            // GPS from geoData
            if (entry.GpsLatitude == null)
            {
                var lat = json["geoData"]?["latitude"]?.Value<double>();
                var lon = json["geoData"]?["longitude"]?.Value<double>();
                var alt = json["geoData"]?["altitude"]?.Value<double>();

                if (lat.HasValue && lon.HasValue && (lat.Value != 0 || lon.Value != 0))
                {
                    entry.GpsLatitude = lat;
                    entry.GpsLongitude = lon;
                    entry.GpsAltitude = alt;
                }
            }
        }
        catch { /* JSON parse failure is non-fatal */ }
    }

    public static string? FindJsonSidecar(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        var fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath); // e.g. ".JPG"

        // 1. Exact: IMG_1234.JPG.json
        var candidate1 = Path.Combine(dir, fileName + ".json");
        if (File.Exists(candidate1)) return candidate1;

        // 2. Extension-stripped: IMG_1234.json
        var candidate2 = Path.Combine(dir, fileNameNoExt + ".json");
        if (File.Exists(candidate2)) return candidate2;

        // 3. Google Takeout duplicate pattern: BASENAME(N).EXT → BASENAME.EXT(N).json
        // e.g. 08_Original(1).JPG → 08_Original.JPG(1).json
        var dupMatch = System.Text.RegularExpressions.Regex.Match(fileNameNoExt, @"^(.+?)(\(\d+\))$");
        if (dupMatch.Success)
        {
            var baseName = dupMatch.Groups[1].Value;
            var suffix = dupMatch.Groups[2].Value;
            var candidate3 = Path.Combine(dir, baseName + ext + suffix + ".json");
            if (File.Exists(candidate3)) return candidate3;
        }

        // 4. Prefix match (Google Takeout truncates long filenames)
        try
        {
            var jsonFiles = Directory.GetFiles(dir, "*.json");
            foreach (var jf in jsonFiles)
            {
                var jName = Path.GetFileNameWithoutExtension(jf); // strips .json
                // jName might be "IMG_1234.JPG" — check if source filename starts with that
                if (fileName.StartsWith(jName, StringComparison.OrdinalIgnoreCase))
                    return jf;
            }
        }
        catch { /* directory listing failed */ }

        return null;
    }
}
