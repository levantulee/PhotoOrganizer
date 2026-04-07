using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class FileOrganizer
{
    public enum FolderMode { YearMonth, YearOnly }

    public string ComputeOutputPath(FileEntry entry, string exportRoot, FolderMode mode, bool isVideo)
    {
        string ext = isVideo ? ".mp4" : ".png";

        if (entry.DateIsUnknown || entry.ResolvedDate == null)
        {
            // Unknown date — preserve original filename in _unknown_date subfolder
            var unknownDir = Path.Combine(exportRoot, "_unknown_date");
            Directory.CreateDirectory(unknownDir);
            var origName = Path.GetFileName(entry.SourcePath);
            // Change extension for images
            if (!isVideo)
                origName = Path.GetFileNameWithoutExtension(origName) + ".png";
            return ResolveCollision(Path.Combine(unknownDir, origName));
        }

        var date = entry.ResolvedDate.Value;
        string folder;
        if (mode == FolderMode.YearMonth)
        {
            folder = Path.Combine(exportRoot, date.Year.ToString(), date.Month.ToString("D2"));
        }
        else
        {
            folder = Path.Combine(exportRoot, date.Year.ToString());
        }

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
            int counter = 2;
            while (true)
            {
                var candidate = Path.Combine(dir, $"{nameNoExt}_{counter}{ext}");
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
