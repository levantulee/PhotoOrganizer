using ImGuiNET;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace PhotoOrganizer.UI;

/// <summary>
/// Scans source, export, processed and failed folders and shows:
///   - A per-extension breakdown of every file type found (photos / videos / others)
///   - A match check: input photos == output photos, input videos == output videos
///   - Orphan JSON report: JSON files with no matching media file in source/processed
///
/// "Processed" originals count as input (moved there after success).
/// "Failed" files count as output (they were attempted).
/// </summary>
public sealed class VerificationPanel
{
    // ── Extension classification ──────────────────────────────────────────────

    private static readonly HashSet<string> PhotoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".heic", ".heif" };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".avi", ".mkv", ".3gp" };

    private enum ExtCategory { Photo, Video, Other }

    private static ExtCategory Classify(string ext)
    {
        if (PhotoExts.Contains(ext)) return ExtCategory.Photo;
        if (VideoExts.Contains(ext)) return ExtCategory.Video;
        return ExtCategory.Other;
    }

    // ── Scan state ────────────────────────────────────────────────────────────

    private bool   _scanning;
    private string _scanStatus = "";
    private bool   _scanned;

    private Dictionary<string, int>? _srcCounts;
    private Dictionary<string, int>? _procCounts;
    private Dictionary<string, int>? _exportCounts;
    private Dictionary<string, int>? _failedCounts;

    // Orphan JSON state
    private List<string>? _orphanJsons;          // full paths
    private bool          _showOrphanList = false;
    private bool          _confirmDelete  = false;

    // ── Public API ────────────────────────────────────────────────────────────

    public void Draw(ref bool show,
                     string sourceFolder, string exportFolder,
                     string processedFolder, string failedFolder)
    {
        if (!show) return;

        ImGui.SetNextWindowSize(new SysVec2(760, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Verification", ref show)) { ImGui.End(); return; }

        FolderLine("Source:   ", sourceFolder);
        FolderLine("Export:   ", exportFolder);
        if (!string.IsNullOrEmpty(processedFolder)) FolderLine("Processed:", processedFolder);
        if (!string.IsNullOrEmpty(failedFolder))    FolderLine("Failed:   ", failedFolder);

        ImGui.Spacing();

        if (_scanning)
        {
            ImGui.TextColored(new SysVec4(1f, 0.8f, 0.2f, 1f), _scanStatus);
        }
        else
        {
            bool canScan = !string.IsNullOrEmpty(sourceFolder) && !string.IsNullOrEmpty(exportFolder);
            if (!canScan) ImGui.BeginDisabled();
            if (ImGui.Button("  Scan Now  "))
                StartScan(sourceFolder, exportFolder, processedFolder, failedFolder);
            if (!canScan) ImGui.EndDisabled();
        }

        ImGui.Separator();

        if (!_scanned)
        {
            ImGui.TextDisabled("Press \"Scan Now\" to check the folders.");
            ImGui.End();
            return;
        }

        DrawOrphanSection();
        ImGui.Spacing();
        DrawResults();
        ImGui.End();
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private void StartScan(string src, string exp, string proc, string fail)
    {
        _scanning       = true;
        _scanned        = false;
        _scanStatus     = "Scanning…";
        _orphanJsons    = null;
        _confirmDelete  = false;
        _srcCounts = _procCounts = _exportCounts = _failedCounts = null;

        Task.Run(() =>
        {
            try
            {
                _scanStatus   = "Scanning source…";
                _srcCounts    = CountByExtension(src, includeSubfolders: true);

                if (!string.IsNullOrEmpty(proc) && Directory.Exists(proc))
                {
                    _scanStatus = "Scanning processed…";
                    _procCounts = CountByExtension(proc, includeSubfolders: true);
                }

                _scanStatus   = "Scanning export…";
                _exportCounts = CountByExtension(exp, includeSubfolders: true);

                if (!string.IsNullOrEmpty(fail) && Directory.Exists(fail))
                {
                    _scanStatus   = "Scanning failed…";
                    _failedCounts = CountByExtension(fail, includeSubfolders: true);
                }

                // Orphan JSON check across source + processed
                _scanStatus = "Finding orphan JSONs…";
                var foldersToCheck = new List<string> { src };
                if (!string.IsNullOrEmpty(proc) && Directory.Exists(proc))
                    foldersToCheck.Add(proc);
                _orphanJsons = FindOrphanJsons(foldersToCheck);
            }
            catch (Exception ex)
            {
                _scanStatus = $"Error: {ex.Message}";
            }
            finally
            {
                _scanning = false;
                _scanned  = true;
            }
        });
    }

    // ── Orphan JSON detection ─────────────────────────────────────────────────

    /// <summary>
    /// For each directory in <paramref name="roots"/> (recursively), finds JSON files
    /// that have no corresponding media file using the same 4-pattern matching as
    /// MetadataService.FindJsonSidecar (but run in reverse: media → JSON claims).
    /// </summary>
    private static List<string> FindOrphanJsons(IEnumerable<string> roots)
    {
        var orphans = new List<string>();

        // Collect all files grouped by directory across all roots
        var byDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(f) ?? "";
                if (!byDir.TryGetValue(dir, out var list))
                    byDir[dir] = list = new List<string>();
                list.Add(f);
            }
        }

        var dupPattern = new System.Text.RegularExpressions.Regex(
            @"^(.+?)(\(\d+\))$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var (_, files) in byDir)
        {
            // Split into JSON files and media files
            var jsonSet = new HashSet<string>(
                files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            if (jsonSet.Count == 0) continue;

            var mediaFiles = files.Where(
                f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var media in mediaFiles)
            {
                var dir          = Path.GetDirectoryName(media) ?? "";
                var fileName     = Path.GetFileName(media);
                var fileNameNoExt = Path.GetFileNameWithoutExtension(media);
                var ext          = Path.GetExtension(media);

                // Pattern 1: exact — photo.jpg → photo.jpg.json
                var c1 = Path.Combine(dir, fileName + ".json");
                if (jsonSet.Contains(c1)) claimed.Add(c1);

                // Pattern 2: stripped — photo.jpg → photo.json
                var c2 = Path.Combine(dir, fileNameNoExt + ".json");
                if (jsonSet.Contains(c2)) claimed.Add(c2);

                // Pattern 3: Google duplicate — photo(1).jpg → photo.jpg(1).json
                var m = dupPattern.Match(fileNameNoExt);
                if (m.Success)
                {
                    var baseName = m.Groups[1].Value;
                    var suffix   = m.Groups[2].Value;
                    var c3 = Path.Combine(dir, baseName + ext + suffix + ".json");
                    if (jsonSet.Contains(c3)) claimed.Add(c3);
                }

                // Pattern 4: prefix match (Google Takeout truncates long filenames)
                foreach (var jf in jsonSet)
                {
                    if (claimed.Contains(jf)) continue;
                    var jName = Path.GetFileNameWithoutExtension(jf); // e.g. "IMG_1234.JPG"
                    if (fileName.StartsWith(jName, StringComparison.OrdinalIgnoreCase))
                        claimed.Add(jf);
                }
            }

            foreach (var jf in jsonSet)
                if (!claimed.Contains(jf))
                    orphans.Add(jf);
        }

        orphans.Sort(StringComparer.OrdinalIgnoreCase);
        return orphans;
    }

    // ── Orphan section UI ─────────────────────────────────────────────────────

    private void DrawOrphanSection()
    {
        if (_orphanJsons == null) return;

        int count = _orphanJsons.Count;

        if (count == 0)
        {
            ImGui.TextColored(new SysVec4(0.4f, 1f, 0.4f, 1f),
                "✓  No orphan JSON files — every JSON has a matching media file.");
            return;
        }

        ImGui.TextColored(new SysVec4(1f, 0.6f, 0.2f, 1f),
            $"⚠  {count} orphan JSON file(s) found (no matching photo/video).");

        ImGui.SameLine();
        if (ImGui.SmallButton(_showOrphanList ? "Hide list" : "Show list"))
        {
            _showOrphanList = !_showOrphanList;
            _confirmDelete  = false;
        }

        ImGui.SameLine();
        if (!_confirmDelete)
        {
            if (ImGui.SmallButton("Delete all orphans…"))
                _confirmDelete = true;
        }
        else
        {
            ImGui.TextColored(new SysVec4(1f, 0.3f, 0.3f, 1f), "Confirm delete?");
            ImGui.SameLine();
            if (ImGui.SmallButton("Yes, delete"))
            {
                DeleteOrphans();
                _confirmDelete = false;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
                _confirmDelete = false;
        }

        if (_showOrphanList && count > 0)
        {
            float listH = Math.Min(count * ImGui.GetTextLineHeightWithSpacing() + 8, 160f);
            ImGui.BeginChild("##orphans", new SysVec2(0, listH),
                ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);

            foreach (var path in _orphanJsons)
                ImGui.TextUnformatted(path);

            ImGui.EndChild();
        }
    }

    private void DeleteOrphans()
    {
        if (_orphanJsons == null) return;
        int deleted = 0;
        var remaining = new List<string>();
        foreach (var path in _orphanJsons)
        {
            try   { File.Delete(path); deleted++; }
            catch { remaining.Add(path); }
        }
        _orphanJsons = remaining;
        _showOrphanList = remaining.Count > 0;
    }

    // ── File count scan ───────────────────────────────────────────────────────

    private static Dictionary<string, int> CountByExtension(string folder, bool includeSubfolders)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return counts;

        var opt = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", opt))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "(none)";
            counts[ext] = counts.GetValueOrDefault(ext) + 1;
        }
        return counts;
    }

    // ── Results table ─────────────────────────────────────────────────────────

    private void DrawResults()
    {
        var input  = Merge(_srcCounts, _procCounts);
        var output = Merge(_exportCounts, _failedCounts);

        int inPhotos  = SumCategory(input,  ExtCategory.Photo);
        int inVideos  = SumCategory(input,  ExtCategory.Video);
        int outPhotos = SumCategory(output, ExtCategory.Photo);
        int outVideos = SumCategory(output, ExtCategory.Video);

        bool photosMatch = inPhotos == outPhotos;
        bool videosMatch = inVideos == outVideos;
        bool allMatch    = photosMatch && videosMatch;

        if (allMatch)
            ImGui.TextColored(new SysVec4(0.4f, 1f, 0.4f, 1f),
                "✓  All photos and videos accounted for.");
        else
        {
            int gap = Math.Abs((inPhotos + inVideos) - (outPhotos + outVideos));
            ImGui.TextColored(new SysVec4(1f, 0.3f, 0.3f, 1f),
                $"✗  Mismatch — {gap} file(s) unaccounted for.");
        }

        ImGui.Spacing();

        var allExts  = new SortedSet<string>(
            input.Keys.Concat(output.Keys), StringComparer.OrdinalIgnoreCase);

        var photoExts = allExts.Where(e => Classify(e) == ExtCategory.Photo).ToList();
        var videoExts = allExts.Where(e => Classify(e) == ExtCategory.Video).ToList();
        var otherExts = allExts.Where(e => Classify(e) == ExtCategory.Other).ToList();

        float tableH = ImGui.GetContentRegionAvail().Y - 4;
        if (!ImGui.BeginTable("##verify", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new SysVec2(0, tableH)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Category",       ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Extension",      ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("In (src+proc)",  ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Out (exp+fail)", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Match",          ImGuiTableColumnFlags.WidthFixed,   55f);
        ImGui.TableHeadersRow();

        DrawGroupHeader("Photos");
        foreach (var ext in photoExts)
            DrawExtRow(ext, input.GetValueOrDefault(ext), output.GetValueOrDefault(ext), matchCheck: true);
        DrawTotalRow("Photo total", inPhotos, outPhotos, photosMatch);

        DrawGroupHeader("Videos");
        foreach (var ext in videoExts)
            DrawExtRow(ext, input.GetValueOrDefault(ext), output.GetValueOrDefault(ext), matchCheck: true);
        DrawTotalRow("Video total", inVideos, outVideos, videosMatch);

        if (otherExts.Count > 0)
        {
            DrawGroupHeader("Others (not converted)");
            foreach (var ext in otherExts)
                DrawExtRow(ext, input.GetValueOrDefault(ext), output.GetValueOrDefault(ext), matchCheck: false);
            int inOther  = SumCategory(input,  ExtCategory.Other);
            int outOther = SumCategory(output, ExtCategory.Other);
            DrawTotalRow("Other total", inOther, outOther, matchCheck: false);
        }

        ImGui.EndTable();
    }

    // ── Table helpers ─────────────────────────────────────────────────────────

    private static void DrawGroupHeader(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
            ImGui.ColorConvertFloat4ToU32(new SysVec4(0.2f, 0.2f, 0.3f, 1f)));
        ImGui.TextDisabled(label);
    }

    private static void DrawExtRow(string ext, int inCount, int outCount, bool matchCheck)
    {
        bool match = inCount == outCount;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted("");
        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(ext);
        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(inCount.ToString());
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(outCount.ToString());
        ImGui.TableSetColumnIndex(4);
        if (matchCheck)
            ImGui.TextColored(match
                ? new SysVec4(0.4f, 1f, 0.4f, 1f)
                : new SysVec4(1f, 0.3f, 0.3f, 1f), match ? "✓" : "✗");
        else
            ImGui.TextDisabled("—");
    }

    private static void DrawTotalRow(string label, int inCount, int outCount, bool matchCheck)
    {
        bool match = inCount == outCount;
        var numColor = matchCheck
            ? (match ? new SysVec4(0.4f, 1f, 0.4f, 1f) : new SysVec4(1f, 0.3f, 0.3f, 1f))
            : new SysVec4(0.7f, 0.7f, 0.7f, 1f);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0); ImGui.Text(label);
        ImGui.TableSetColumnIndex(1); ImGui.TextDisabled("(all)");
        ImGui.TableSetColumnIndex(2); ImGui.TextColored(numColor, inCount.ToString());
        ImGui.TableSetColumnIndex(3); ImGui.TextColored(numColor, outCount.ToString());
        ImGui.TableSetColumnIndex(4);
        if (matchCheck)
            ImGui.TextColored(match
                ? new SysVec4(0.4f, 1f, 0.4f, 1f)
                : new SysVec4(1f, 0.3f, 0.3f, 1f), match ? "✓" : "✗");
        else
            ImGui.TextDisabled("—");
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static Dictionary<string, int> Merge(
        Dictionary<string, int>? a, Dictionary<string, int>? b)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (a != null) foreach (var kv in a) result[kv.Key] = result.GetValueOrDefault(kv.Key) + kv.Value;
        if (b != null) foreach (var kv in b) result[kv.Key] = result.GetValueOrDefault(kv.Key) + kv.Value;
        return result;
    }

    private static int SumCategory(Dictionary<string, int> counts, ExtCategory cat)
        => counts.Where(kv => Classify(kv.Key) == cat).Sum(kv => kv.Value);

    private static void FolderLine(string label, string path)
    {
        ImGui.TextDisabled(label); ImGui.SameLine();
        ImGui.TextUnformatted(string.IsNullOrEmpty(path) ? "(not set)" : path);
    }
}
