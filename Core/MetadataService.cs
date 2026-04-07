using ImageMagick;
using Newtonsoft.Json.Linq;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class MetadataService
{
    public FileEntry Resolve(string filePath)
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
                entry.ResolvedDate = File.GetLastWriteTime(filePath);
                entry.DateIsUnknown = true;
            }
            return entry;
        }

        // Images: try EXIF first
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
            entry.ResolvedDate = File.GetLastWriteTime(filePath);
            entry.DateIsUnknown = true;
        }

        return entry;
    }

    public static FileCategory ClassifyFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" => FileCategory.Image,
            ".heic" or ".heif" => FileCategory.Heic,
            ".mov" or ".mp4" or ".avi" or ".mkv" or ".3gp" => FileCategory.Video,
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
