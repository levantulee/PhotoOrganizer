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
    public bool InferMissingDates { get; set; } = true;
    /// <summary>Source paths already successfully processed; these files are skipped.</summary>
    public HashSet<string>? AlreadyProcessed { get; set; }
}

public class Processor
{
    private readonly MetadataService _metadata = new();
    private readonly FileOrganizer _organizer = new();
    private readonly VideoConverter _video = new();

    // Gate: open = running, closed = paused. Active conversions finish; new ones block here.
    private readonly ManualResetEventSlim _pauseGate = new(true);

    // Lazy per-folder inference cache: populated only when an unknown-date file is encountered.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileEntry[]>
        _folderInferenceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _folderSemaphores = new(StringComparer.OrdinalIgnoreCase);

    // Dynamic concurrency throttle — replaced each run, adjusted at any time during a run.
    private DynamicThrottle? _throttle;

    public bool IsPaused => !_pauseGate.IsSet;

    public void Pause()  => _pauseGate.Reset();
    public void Resume() => _pauseGate.Set();

    /// <summary>
    /// Changes the number of concurrent conversions while a run is in progress.
    /// Safe to call from any thread at any time.
    /// Reducing closes slots as workers finish their current file; increasing opens slots immediately.
    /// </summary>
    public void SetParallelism(int count) => _throttle?.SetCount(count);

    public event Action<ProcessResult>? Progress;
    public event Action<string>? FileStarted;

    public async Task RunAsync(ProcessorOptions opts, CancellationToken ct)
    {
        FileOrganizer.ClearReservedPaths();

        var searchOption = opts.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(opts.SourceFolder, "*.*", searchOption)
            .Where(f => !Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        EmitInfo($"Processing {files.Count} files…");

        _folderInferenceCache.Clear();
        _folderSemaphores.Clear();
        _throttle = new DynamicThrottle(opts.Parallelism);

        // Prevent ImageMagick from spawning its own internal thread pool per operation.
        // Without this, each MagickImage call can use many OS threads internally,
        // blowing past the throttle limit and pegging all cores.
        ResourceLimits.Thread = 1;

        // Each file waits for a throttle slot, then dispatches its work to a real
        // thread-pool thread via Task.Run so CPU-bound work runs truly in parallel.
        // Without Task.Run the async lambdas execute synchronously on the caller's
        // thread until the first genuine I/O yield, making all conversions sequential.
        var tasks = files.Select(async file =>
        {
            await _throttle.WaitAsync(ct);
            try
            {
                await Task.Run(async () =>
                {
                    _pauseGate.Wait(ct);
                    ct.ThrowIfCancellationRequested();
                    FileStarted?.Invoke(file);

                    var entry = _metadata.Resolve(file);

                    if (entry.DateIsUnknown && opts.InferMissingDates)
                        await TryInferDateFromFolderAsync(entry, ct);

                    Progress?.Invoke(await ProcessFileAsync(entry, opts, ct));
                }, ct);
            }
            finally
            {
                _throttle.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Release inference cache memory once the run is done
        _folderInferenceCache.Clear();
        _folderSemaphores.Clear();
        _throttle = null;
    }

    // ── Dynamic concurrency throttle ──────────────────────────────────────────

    /// <summary>
    /// A semaphore whose count can be raised or lowered at runtime.
    /// Raising: immediately releases extra permits so waiting tasks can start.
    /// Lowering: absorbs the next N releases so slots drain naturally as workers finish.
    /// </summary>
    private sealed class DynamicThrottle
    {
        private readonly SemaphoreSlim _sem;
        private int _current;
        private int _toAbsorb;
        private readonly object _lock = new();

        public DynamicThrottle(int initial)
        {
            _current = Math.Max(1, initial);
            _sem = new SemaphoreSlim(_current, 1024);
        }

        public Task WaitAsync(CancellationToken ct) => _sem.WaitAsync(ct);

        public void Release()
        {
            lock (_lock)
            {
                if (_toAbsorb > 0) { _toAbsorb--; return; }
                _sem.Release();
            }
        }

        public void SetCount(int newCount)
        {
            newCount = Math.Max(1, newCount);
            lock (_lock)
            {
                int diff = newCount - _current;
                _current = newCount;

                if (diff > 0)
                {
                    // Cancel pending absorptions first, then release any remainder
                    int cancel = Math.Min(_toAbsorb, diff);
                    _toAbsorb -= cancel;
                    diff      -= cancel;
                    if (diff > 0) _sem.Release(diff);
                }
                else if (diff < 0)
                {
                    // Schedule absorptions — slots drain as workers finish
                    _toAbsorb += -diff;
                }
            }
        }
    }

    /// <summary>
    /// Scans the folder of <paramref name="entry"/> (TopDirectoryOnly) exactly once,
    /// runs date inference across all files in it, then copies the inferred date back
    /// onto <paramref name="entry"/> if one was found.
    /// Subsequent calls for the same folder use the cached result — no double work.
    /// </summary>
    private async ValueTask TryInferDateFromFolderAsync(FileEntry entry, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(entry.SourcePath) ?? "";

        // Fast path: already cached
        if (_folderInferenceCache.TryGetValue(folder, out var cached))
        {
            ApplyInferred(entry, cached);
            return;
        }

        // One thread scans per folder; others wait then use the cache
        var sem = _folderSemaphores.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (!_folderInferenceCache.TryGetValue(folder, out cached))
            {
                var folderEntries = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => !Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(f => _metadata.Resolve(f))
                    .ToList();

                MetadataService.InferMissingDates(folderEntries);
                cached = folderEntries.ToArray();
                _folderInferenceCache[folder] = cached;
            }
        }
        finally { sem.Release(); }

        ApplyInferred(entry, cached);
    }

    private static void ApplyInferred(FileEntry entry, FileEntry[] folderEntries)
    {
        var match = Array.Find(folderEntries,
            e => string.Equals(e.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase));

        if (match?.DateIsEstimated == true)
        {
            entry.ResolvedDate    = match.ResolvedDate;
            entry.DateIsUnknown   = false;
            entry.DateIsEstimated = true;
        }
    }

    private void EmitInfo(string message) =>
        Progress?.Invoke(new ProcessResult
        {
            Entry   = new FileEntry(),
            Status  = ResultStatus.Skipped,
            Message = message
        });

    private async Task<ProcessResult> ProcessFileAsync(FileEntry entry, ProcessorOptions opts, CancellationToken ct)
    {
        var filePath = entry.SourcePath;
        var result = new ProcessResult { Entry = entry };
        try
        {
            // Skip if already processed successfully in a previous run
            if (opts.AlreadyProcessed?.Contains(filePath) == true)
            {
                result.Status  = ResultStatus.Skipped;
                result.Message = $"Already processed: {Path.GetFileName(filePath)}";
                return result;
            }

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
                var srcExt = Path.GetExtension(filePath).ToLowerInvariant();

                if (srcExt == ".png")
                {
                    // PNG → PNG: straight copy, no re-encoding
                    File.Copy(filePath, outputPath, overwrite: false);
                    result.WasConverted = false;

                    if (!IntegrityChecker.VerifyConvertedImage(outputPath))
                        throw new Exception("PNG copy integrity check failed.");
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

                // --- EXIF diagnostic ---
                byte[]? fromFile    = ExtractRawExifFromJpeg(filePath);
                byte[]? fromProfile = exifProfile.ToByteArray();
                byte[]? rawExif     = fromFile ?? fromProfile;

                string make  = exifProfile.GetValue(ExifTag.Make)?.Value  ?? "(none)";
                string model = exifProfile.GetValue(ExifTag.Model)?.Value ?? "(none)";
                string dto   = exifProfile.GetValue(ExifTag.DateTimeOriginal)?.Value ?? "(none)";
                Progress?.Invoke(new ProcessResult
                {
                    Entry   = entry,
                    Status  = ResultStatus.Skipped,
                    Message = $"[EXIF diag] file={FmtBytes(fromFile)} profile={FmtBytes(fromProfile)} " +
                              $"Make={make} Model={model} DTO={dto}"
                });

                img.Format = MagickFormat.Png;
                await img.WriteAsync(outputPath, ct);

                string injectResult;
                byte[]? injectedPng = null;
                if (rawExif is { Length: > 0 })
                    (injectResult, injectedPng) = TryInjectExifIntoPng(outputPath, rawExif);
                else
                    injectResult = "skip:no-raw-exif";

                // Verify round-trip using the already-in-memory bytes — no second disk read.
                byte[]? pngExif = injectedPng != null
                    ? FindExifChunkInBytes(injectedPng)
                    : ReadExifChunkFromPng(outputPath);
                bool match = rawExif != null && pngExif != null && rawExif.Length == pngExif.Length;

                // Release the large buffers as soon as we're done with them
                injectedPng = null;
                rawExif     = null;
                fromFile    = null;
                fromProfile = null;
                Progress?.Invoke(new ProcessResult
                {
                    Entry   = entry,
                    Status  = ResultStatus.Skipped,
                    Message = $"[EXIF cmp] inject={injectResult} " +
                              $"src={FmtBytes(rawExif)} png={FmtBytes(pngExif)} match={match}"
                });

                result.WasConverted = entry.Category == FileCategory.Heic
                    || srcExt is ".tif" or ".tiff";

                if (!IntegrityChecker.VerifyConvertedImage(outputPath))
                    throw new Exception("Converted image integrity check failed.");
                } // end Magick.NET branch
            }

            // 4. Move source (and any JSON sidecar) to Processed
            MoveToProcessed(filePath, opts.SourceFolder, opts.ProcessedFolder);
            if (!string.IsNullOrEmpty(entry.JsonSidecarPath))
                MoveToProcessed(entry.JsonSidecarPath, opts.SourceFolder, opts.ProcessedFolder);

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
        var srcExtLower = Path.GetExtension(entry.SourcePath).ToLowerInvariant();
        string suffix = entry.Category switch
        {
            FileCategory.Heic => " (heic→png)",
            FileCategory.Video => " (conv)",
            FileCategory.Image when srcExtLower is ".tif" or ".tiff" => " (tiff→png)",
            _ => ""
        };
        string dateTag = entry.DateIsUnknown   ? " ⚠ no date"   :
                         entry.DateIsEstimated ? " ~ est. date" : "";
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

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private static string FmtBytes(byte[]? b)
    {
        if (b == null)            return "null";
        if (b.Length == 0)        return "empty";
        string hdr = b.Length >= 4
            ? $"{b[0]:X2}{b[1]:X2}{b[2]:X2}{b[3]:X2}"
            : BitConverter.ToString(b).Replace("-", "");
        return $"{b.Length}b[{hdr}]";
    }

    // ── Raw EXIF extraction ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the raw TIFF bytes from a JPEG APP1/Exif segment.
    /// Returns null if the file is not a JPEG or has no Exif APP1.
    /// </summary>
    private static byte[]? ExtractRawExifFromJpeg(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4];

            // Must start with JPEG SOI 0xFF 0xD8
            if (fs.Read(buf, 0, 2) != 2 || buf[0] != 0xFF || buf[1] != 0xD8)
                return null;

            while (true)
            {
                // Each segment marker starts with 0xFF
                if (fs.Read(buf, 0, 2) != 2 || buf[0] != 0xFF) return null;
                byte marker = buf[1];
                while (marker == 0xFF) // skip padding
                {
                    if (fs.Read(buf, 0, 1) != 1) return null;
                    marker = buf[0];
                }

                if (marker == 0xD9 || marker == 0xDA) return null; // EOI / SOS

                // Segment length field includes its own 2 bytes
                if (fs.Read(buf, 0, 2) != 2) return null;
                int segLen = (buf[0] << 8) | buf[1];
                if (segLen < 2) return null;

                if (marker == 0xE1) // APP1
                {
                    var data = new byte[segLen - 2];
                    if (fs.Read(data, 0, data.Length) != data.Length) return null;

                    // Must start with "Exif\0\0"
                    if (data.Length >= 6 &&
                        data[0] == 'E' && data[1] == 'x' && data[2] == 'i' &&
                        data[3] == 'f' && data[4] == 0   && data[5] == 0)
                    {
                        return data[6..]; // raw TIFF — "II*\0..." or "MM\0*..."
                    }
                    // Could be XMP APP1 — skip it and keep looking
                }

                fs.Seek(segLen - 2, SeekOrigin.Current);
            }
        }
        catch { return null; }
    }

    // ── PNG eXIf chunk injection ──────────────────────────────────────────────

    /// <summary>
    /// Strips any existing eXIf chunks then injects our raw TIFF bytes as a new one
    /// right after IHDR. Returns a short status string for diagnostic logging.
    /// </summary>
    /// <summary>
    /// Injects raw TIFF EXIF into a PNG file as an eXIf chunk.
    /// Returns the status string and the final PNG bytes (already written to disk).
    /// Caller can use the returned bytes for verification without a second disk read.
    /// </summary>
    private static (string status, byte[]? pngBytes) TryInjectExifIntoPng(string pngPath, byte[] exifData)
    {
        try
        {
            // Strip "Exif\0\0" JPEG APP1 prefix — PNG eXIf payload is raw TIFF.
            if (exifData.Length > 6 &&
                exifData[0] == 'E' && exifData[1] == 'x' && exifData[2] == 'i' &&
                exifData[3] == 'f' && exifData[4] == 0   && exifData[5] == 0)
                exifData = exifData[6..];

            if (exifData.Length < 4 ||
                !((exifData[0] == 'I' && exifData[1] == 'I') ||
                  (exifData[0] == 'M' && exifData[1] == 'M')))
                return ($"skip:bad-tiff-header {exifData[0]:X2}{exifData[1]:X2}", null);

            byte[] png = File.ReadAllBytes(pngPath);

            ReadOnlySpan<byte> sig = new byte[]
                { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(sig))
                return ("skip:not-png", null);

            const uint TAG_eXIf = 0x65584966u;
            bool stripped;
            (png, stripped) = StripPngChunks(png, TAG_eXIf);

            int ihdrDataLen = PngReadInt32(png, 8);
            int insertAt    = 8 + 4 + 4 + ihdrDataLen + 4;

            byte[] typeBytes = { 0x65, 0x58, 0x49, 0x66 };
            uint   crc       = PngCrc32(typeBytes, exifData);

            byte[] chunk = new byte[4 + 4 + exifData.Length + 4];
            PngWriteInt32(chunk, 0, exifData.Length);
            typeBytes.CopyTo(chunk, 4);
            exifData.CopyTo(chunk, 8);
            PngWriteInt32(chunk, 8 + exifData.Length, (int)crc);

            byte[] result = new byte[png.Length + chunk.Length];
            png.AsSpan(0, insertAt).CopyTo(result);
            chunk.CopyTo(result, insertAt);
            png.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));

            // Release the intermediate buffer before writing
            png = null!;

            File.WriteAllBytes(pngPath, result);
            return ($"ok:{exifData.Length}b{(stripped ? " (replaced existing)" : "")}", result);
        }
        catch (Exception ex)
        {
            return ($"error:{ex.Message}", null);
        }
    }

    /// <summary>Reads a PNG from disk and returns the payload of the first eXIf chunk, or null.</summary>
    private static byte[]? ReadExifChunkFromPng(string pngPath)
    {
        try
        {
            return FindExifChunkInBytes(File.ReadAllBytes(pngPath));
        }
        catch { return null; }
    }

    /// <summary>Scans already-loaded PNG bytes for the first eXIf chunk payload — no disk I/O.</summary>
    private static byte[]? FindExifChunkInBytes(byte[] png)
    {
        try
        {
            ReadOnlySpan<byte> sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(sig)) return null;

            const uint TAG_eXIf = 0x65584966u;
            const uint TAG_IEND = 0x49454E44u;
            int pos = 8;
            while (pos + 12 <= png.Length)
            {
                int  dataLen = PngReadInt32(png, pos);
                uint type    = (uint)PngReadInt32(png, pos + 4);
                if (type == TAG_eXIf && pos + 8 + dataLen <= png.Length)
                    return png[(pos + 8)..(pos + 8 + dataLen)];
                if (type == TAG_IEND) break;
                pos += 4 + 4 + dataLen + 4;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Removes all chunks matching chunkTag; returns modified bytes and whether any were removed.</summary>
    private static (byte[] png, bool stripped) StripPngChunks(byte[] png, uint chunkTag)
    {
        bool found = false;
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int  dataLen  = PngReadInt32(png, pos);
            uint type     = (uint)PngReadInt32(png, pos + 4);
            if (type == chunkTag) { found = true; break; }
            if (type == 0x49454E44u) break; // IEND
            pos += 4 + 4 + dataLen + 4;
        }
        if (!found) return (png, false);

        using var ms = new MemoryStream(png.Length);
        ms.Write(png, 0, 8); // signature
        pos = 8;
        while (pos + 12 <= png.Length)
        {
            int  dataLen = PngReadInt32(png, pos);
            uint type    = (uint)PngReadInt32(png, pos + 4);
            int  total   = 4 + 4 + dataLen + 4;
            if (pos + total > png.Length) break;
            if (type != chunkTag)
                ms.Write(png, pos, total);
            pos += total;
            if (type == 0x49454E44u) break; // IEND
        }
        return (ms.ToArray(), true);
    }

    private static int PngReadInt32(byte[] d, int i) =>
        (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

    private static void PngWriteInt32(byte[] d, int i, int v)
    {
        // Cast to uint before shifting — int >> is arithmetic (sign-extends),
        // which corrupts any byte where bit 7 is set.
        uint u   = (uint)v;
        d[i]     = (byte)(u >> 24);
        d[i + 1] = (byte)(u >> 16);
        d[i + 2] = (byte)(u >> 8);
        d[i + 3] = (byte)u;
    }

    // Standard CRC-32 (PNG uses CRC over chunk type + chunk data).
    private static readonly uint[] _crcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint PngCrc32(byte[] a, byte[] b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in a) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (byte x in b) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static string GetUniqueFilePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int i = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name}_{i:D4}{ext}");
            i++;
        }
        return path;
    }
}
