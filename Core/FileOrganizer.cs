using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class FileOrganizer
{
    public enum FolderMode { YearMonth, YearOnly }

    /// <summary>
    /// Computes the output path for a file entry.
    /// <para>
    /// - <see cref="FileCategory.Video"/>: outputs as <c>.mp4</c> (converted), or original ext when <paramref name="organizeOnlyVideos"/> is true.<br/>
    /// - <see cref="FileCategory.Image"/> / <see cref="FileCategory.Heic"/>: outputs as <c>.png</c> (converted), or original ext when <paramref name="organizeOnlyPhotos"/> is true.<br/>
    /// - <see cref="FileCategory.Raw"/>: always keeps original ext, placed in <paramref name="rawSubfolder"/> within <paramref name="exportRoot"/>.<br/>
    /// - <see cref="FileCategory.PassThrough"/>: always keeps original ext, placed with regular images.
    /// </para>
    /// </summary>
    public string ComputeOutputPath(FileEntry entry, string exportRoot, FolderMode mode,
        bool organizeOnlyPhotos = false, bool organizeOnlyVideos = false, string rawSubfolder = "_RAW")
    {
        string srcExt = Path.GetExtension(entry.SourcePath).ToLowerInvariant();

        string ext = entry.Category switch
        {
            FileCategory.Video => organizeOnlyVideos ? srcExt : ".mp4",
            FileCategory.Image or FileCategory.Heic => organizeOnlyPhotos ? srcExt : ".png",
            _ => srcExt  // Raw, PassThrough: always keep original extension
        };

        bool isRaw = entry.Category == FileCategory.Raw;
        string rawFolder = string.IsNullOrWhiteSpace(rawSubfolder) ? "_RAW" : rawSubfolder.Trim();

        if (entry.DateIsUnknown || entry.ResolvedDate == null)
        {
            var unknownDir = isRaw
                ? Path.Combine(exportRoot, rawFolder, "_unknown_date")
                : Path.Combine(exportRoot, "_unknown_date");
            Directory.CreateDirectory(unknownDir);
            var origName = Path.GetFileNameWithoutExtension(entry.SourcePath) + ext;
            return ResolveCollision(Path.Combine(unknownDir, origName));
        }

        var date = entry.ResolvedDate.Value;
        string dateSubPath = mode == FolderMode.YearMonth
            ? Path.Combine(date.Year.ToString(), date.Month.ToString("D2"))
            : date.Year.ToString();

        string folder = isRaw
            ? Path.Combine(exportRoot, rawFolder, dateSubPath)
            : Path.Combine(exportRoot, dateSubPath);

        Directory.CreateDirectory(folder);

        var baseName = date.ToString("yyyy-MM-dd_HH-mm-ss") + ext;
        return ResolveCollision(Path.Combine(folder, baseName));
    }

    // Tracks paths claimed by in-flight parallel tasks so two threads never get the same output path.
    private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _reservedLock = new();

    public static void ClearReservedPaths() { lock (_reservedLock) _reserved.Clear(); }

    private static string ResolveCollision(string path)
    {
        lock (_reservedLock)
        {
            if (!File.Exists(path) && _reserved.Add(path))
                return path;

            var dir = Path.GetDirectoryName(path) ?? "";
            var nameNoExt = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int counter = 1;
            while (true)
            {
                var candidate = Path.Combine(dir, $"{nameNoExt}_{counter:D4}{ext}");
                if (!File.Exists(candidate) && _reserved.Add(candidate))
                    return candidate;
                counter++;
            }
        }
    }

    public string ComputeProcessedPath(string sourceFile, string sourceRoot, string processedRoot)
    {
        // Preserve relative subdirectory structure
        try
        {
            var rel = Path.GetRelativePath(sourceRoot, sourceFile);
            var dest = Path.Combine(processedRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            return dest;
        }
        catch
        {
            // Fallback: flat
            return Path.Combine(processedRoot, Path.GetFileName(sourceFile));
        }
    }

    public string ComputeFailedPath(string sourceFile, string failedRoot)
    {
        Directory.CreateDirectory(failedRoot);
        return Path.Combine(failedRoot, Path.GetFileName(sourceFile));
    }
}
