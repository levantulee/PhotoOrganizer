using ImageMagick;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class ProcessorOptions
{
    public string SourceFolder { get; set; } = "";
    public string ExportFolder { get; set; } = "";
    public string ProcessedFolder { get; set; } = "";
    public string FailedFolder { get; set; } = "";
    public FileOrganizer.FolderMode FolderMode { get; set; } = FileOrganizer.FolderMode.YearMonth;
    public bool IncludeSubfolders { get; set; } = true;
    public int Parallelism { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
}

public class Processor
{
    private readonly MetadataService _metadata = new();
    private readonly FileOrganizer _organizer = new();
    private readonly VideoConverter _video = new();

    public event Action<ProcessResult>? Progress;

    public async Task RunAsync(ProcessorOptions opts, CancellationToken ct)
    {
        FileOrganizer.ClearReservedPaths();

        var searchOption = opts.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(opts.SourceFolder, "*.*", searchOption)
            .Where(f => !Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = opts.Parallelism, CancellationToken = ct },
            async (file, token) =>
            {
                var result = await ProcessFileAsync(file, opts, token);
                Progress?.Invoke(result);
            });
    }

    private async Task<ProcessResult> ProcessFileAsync(string filePath, ProcessorOptions opts, CancellationToken ct)
    {
        var result = new ProcessResult();
        try
        {
            // 1. Classify & resolve metadata
            var entry = _metadata.Resolve(filePath);
            result.Entry = entry;

            if (entry.Category == FileCategory.Unknown)
            {
                result.Status = ResultStatus.Skipped;
                result.Message = $"Unsupported type: {Path.GetExtension(filePath)}";
                MoveSource(filePath, opts.FailedFolder, opts.SourceFolder);
                return result;
            }

            // 2. Compute output path
            bool isVideo = entry.Category == FileCategory.Video;
            string outputPath = _organizer.ComputeOutputPath(entry, opts.ExportFolder, opts.FolderMode, isVideo);
            result.OutputPath = outputPath;

            // 3. Convert / copy
            if (isVideo)
            {
                if (!VideoConverter.IsFfmpegAvailable)
                {
                    result.Status = ResultStatus.Skipped;
                    result.Message = "FFmpeg not available — skipped video";
                    return result;
                }
                await _video.ConvertToMp4Async(filePath, outputPath, entry.ResolvedDate, ct);
                result.WasConverted = true;

                // Verify
                if (!await IntegrityChecker.VerifyVideoAsync(outputPath))
                    throw new Exception("Video integrity check failed.");
            }
            else
            {
                // Image or HEIC — convert to PNG via Magick.NET
                using var img = new MagickImage(filePath);

                // --- Preserve ALL profiles from original ---
                // EXIF: update date/GPS but keep every other original tag
                var exifProfile = img.GetExifProfile() ?? new ExifProfile();

                if (entry.ResolvedDate.HasValue)
                {
                    string exifDate = entry.ResolvedDate.Value.ToString("yyyy:MM:dd HH:mm:ss");
                    exifProfile.SetValue(ExifTag.DateTimeOriginal, exifDate);
                    exifProfile.SetValue(ExifTag.DateTimeDigitized, exifDate);
                    exifProfile.SetValue(ExifTag.DateTime, exifDate);
                }

                if (entry.GpsLatitude.HasValue && entry.GpsLongitude.HasValue)
                    WriteGpsToExif(exifProfile, entry.GpsLatitude.Value, entry.GpsLongitude.Value, entry.GpsAltitude);

                img.SetProfile(exifProfile);

                // XMP profile — keep as-is from original (contains camera/edit history etc.)
                // Magick.NET preserves it automatically in the MagickImage object;
                // SetProfile is only needed for profiles we explicitly modified.

                // Tell the PNG encoder to include ALL metadata chunks (eXIf, iTXt/XMP, iCCP, etc.)
                img.Settings.SetDefine("png:include-chunk", "all");

                img.Format = MagickFormat.Png;
                await img.WriteAsync(outputPath, ct);

                result.WasConverted = entry.Category == FileCategory.Heic;

                // Verify
                if (entry.Category == FileCategory.Image && !result.WasConverted)
                {
                    // For straight PNG copy (was PNG input): verify readable
                    if (!IntegrityChecker.VerifyConvertedImage(outputPath))
                        throw new Exception("Output image integrity check failed.");
                }
                else
                {
                    if (!IntegrityChecker.VerifyConvertedImage(outputPath))
                        throw new Exception("Converted image integrity check failed.");
                }
            }

            // 4. Move source to Processed
            MoveToProcessed(filePath, opts.SourceFolder, opts.ProcessedFolder);

            result.Status = ResultStatus.Success;
            result.Message = BuildSuccessMessage(entry, outputPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Status = ResultStatus.Failed;
            result.Message = $"{Path.GetFileName(filePath)} — {ex.Message}";
            TryMoveToFailed(filePath, opts.FailedFolder);
        }
        return result;
    }

    private static string BuildSuccessMessage(FileEntry entry, string outputPath)
    {
        var outName = Path.GetFileName(outputPath);
        var srcName = Path.GetFileName(entry.SourcePath);
        string suffix = entry.Category switch
        {
            FileCategory.Heic => " (heic→png)",
            FileCategory.Video => " (conv)",
            _ => ""
        };
        string dateTag = entry.DateIsUnknown ? " ⚠ no date" : "";
        return $"{outName}  ←  {srcName}{suffix}{dateTag}";
    }

    private static void WriteGpsToExif(IExifProfile exif, double lat, double lon, double? alt)
    {
        exif.SetValue(ExifTag.GPSLatitudeRef, lat >= 0 ? "N" : "S");
        exif.SetValue(ExifTag.GPSLatitude, DegreesToRationals(Math.Abs(lat)));
        exif.SetValue(ExifTag.GPSLongitudeRef, lon >= 0 ? "E" : "W");
        exif.SetValue(ExifTag.GPSLongitude, DegreesToRationals(Math.Abs(lon)));
        if (alt.HasValue)
        {
            exif.SetValue(ExifTag.GPSAltitudeRef, (byte)(alt.Value < 0 ? 1 : 0));
            exif.SetValue(ExifTag.GPSAltitude, new Rational((uint)(Math.Abs(alt.Value) * 1000), 1000));
        }
    }

    private static Rational[] DegreesToRationals(double degrees)
    {
        int deg = (int)degrees;
        double minFull = (degrees - deg) * 60;
        int min = (int)minFull;
        double sec = (minFull - min) * 60;
        return new[]
        {
            new Rational((uint)deg, 1),
            new Rational((uint)min, 1),
            new Rational((uint)(sec * 1000), 1000)
        };
    }

    private static void MoveToProcessed(string sourcePath, string sourceRoot, string processedRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(processedRoot)) return;
            Directory.CreateDirectory(processedRoot);
            string rel;
            try { rel = Path.GetRelativePath(sourceRoot, sourcePath); }
            catch { rel = Path.GetFileName(sourcePath); }
            var dest = Path.Combine(processedRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) dest = GetUniqueFilePath(dest);
            File.Move(sourcePath, dest);
        }
        catch { /* move failure is non-fatal */ }
    }

    private static void MoveSource(string sourcePath, string failedRoot, string sourceRoot)
    {
        TryMoveToFailed(sourcePath, failedRoot);
    }

    private static void TryMoveToFailed(string sourcePath, string failedRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(failedRoot)) return;
            Directory.CreateDirectory(failedRoot);
            var dest = Path.Combine(failedRoot, Path.GetFileName(sourcePath));
            if (File.Exists(dest)) dest = GetUniqueFilePath(dest);
            File.Move(sourcePath, dest);
        }
        catch { /* move failure is non-fatal */ }
    }

    private static string GetUniqueFilePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int i = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name}_{i}{ext}");
            i++;
        }
        return path;
    }
}
