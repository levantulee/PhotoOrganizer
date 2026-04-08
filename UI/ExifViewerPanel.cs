using ImageMagick;
using ImageMagick.Formats;
using ImGuiNET;
using PhotoOrganizer.Core;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace PhotoOrganizer.UI;

/// <summary>
/// Standalone EXIF viewer panel: browse a folder, pick a photo, view all EXIF tags.
/// </summary>
public class ExifViewerPanel
{
    // Folder browse
    private byte[] _folderBuf = new byte[1024];
    private string _currentFolder = "";

    // File list
    private List<string> _files = new();
    private int _selectedFile = -1;
    private string _selectedPath = "";

    // EXIF display
    private List<(string Tag, string Value)> _exifRows = new();
    private string _exifError = "";
    private byte[] _filterBuf = new byte[128];

    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tiff", ".tif", ".bmp", ".webp" };

    public void Draw(ref bool open)
    {
        if (!open) return;

        ImGui.SetNextWindowSize(new SysVec2(900, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("EXIF Viewer", ref open))
        {
            ImGui.End();
            return;
        }

        // ── Top row: folder input + browse ───────────────────────────────────
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90f);
        bool folderChanged = ImGui.InputText("##evFolder", _folderBuf, (uint)_folderBuf.Length,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Browse##ev") || folderChanged)
        {
            if (folderChanged)
                _currentFolder = ReadString(_folderBuf);
            else
                BrowseFolder();
            RefreshFileList();
        }

        ImGui.Separator();

        float leftPaneWidth = 260f;
        float rightPaneWidth = ImGui.GetContentRegionAvail().X - leftPaneWidth - ImGui.GetStyle().ItemSpacing.X;

        // ── Left pane: file list ──────────────────────────────────────────────
        ImGui.BeginChild("##evFileList", new SysVec2(leftPaneWidth, 0), ImGuiChildFlags.Border);

        if (_files.Count == 0)
        {
            ImGui.TextDisabled(_currentFolder.Length > 0 ? "No images found." : "Select a folder.");
        }
        else
        {
            for (int i = 0; i < _files.Count; i++)
            {
                string name = Path.GetFileName(_files[i]);
                bool selected = _selectedFile == i;
                if (ImGui.Selectable(name, selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedFile = i;
                    _selectedPath = _files[i];
                    LoadExif(_selectedPath);
                }
                if (selected && ImGui.IsItemVisible())
                    ImGui.SetScrollHereY(0.5f);
            }
        }

        ImGui.EndChild();

        ImGui.SameLine();

        // ── Right pane: EXIF data ─────────────────────────────────────────────
        ImGui.BeginChild("##evExif", new SysVec2(rightPaneWidth, 0), ImGuiChildFlags.Border);

        if (_selectedPath.Length > 0)
        {
            ImGui.TextColored(new SysVec4(0.4f, 0.8f, 1f, 1f), Path.GetFileName(_selectedPath));

            if (_exifError.Length > 0)
            {
                ImGui.TextColored(new SysVec4(1f, 0.3f, 0.3f, 1f), _exifError);
            }
            else if (_exifRows.Count == 0)
            {
                ImGui.TextDisabled("No EXIF data found.");
            }
            else
            {
                // Filter
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputText("##evFilter", _filterBuf, (uint)_filterBuf.Length);
                string filter = ReadString(_filterBuf).Trim();

                ImGui.Separator();

                float tableHeight = ImGui.GetContentRegionAvail().Y - 4f;
                if (ImGui.BeginTable("##exifTable", 2,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                    new SysVec2(0, tableHeight)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Tag",   ImGuiTableColumnFlags.WidthFixed,   200f);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableHeadersRow();

                    foreach (var (tag, value) in _exifRows)
                    {
                        if (filter.Length > 0 &&
                            !tag.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                            !value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted(tag);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(value);
                    }

                    ImGui.EndTable();
                }
            }
        }
        else
        {
            ImGui.TextDisabled("Select an image from the list.");
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private void RefreshFileList()
    {
        _files.Clear();
        _selectedFile = -1;
        _selectedPath = "";
        _exifRows.Clear();
        _exifError = "";

        if (!Directory.Exists(_currentFolder)) return;

        try
        {
            _files = Directory.EnumerateFiles(_currentFolder)
                .Where(f => ImageExtensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { /* access denied etc. */ }
    }

    private void LoadExif(string path)
    {
        _exifRows.Clear();
        _exifError = "";

        try
        {
            bool isPng = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

            using var image = new MagickImage(path);

            // --- standard EXIF profile ---
            var exif = image.GetExifProfile();
            if (exif != null)
            {
                foreach (var val in exif.Values)
                {
                    string tag   = val.Tag.ToString();
                    string value = FormatExifValue(val);
                    _exifRows.Add((tag, value));
                }
            }

            // --- XMP profile (extra metadata Google Takeout / Lightroom writes) ---
            var xmp = image.GetXmpProfile();
            if (xmp != null)
            {
                _exifRows.Add(("--- XMP ---", ""));
                try
                {
                    var xmpText = System.Text.Encoding.UTF8.GetString(xmp.ToByteArray() ?? Array.Empty<byte>());
                    // Pull out a few useful XMP fields rather than dumping raw XML
                    ExtractXmpField(xmpText, "xmp:CreateDate",          _exifRows);
                    ExtractXmpField(xmpText, "photoshop:DateCreated",    _exifRows);
                    ExtractXmpField(xmpText, "dc:description",           _exifRows);
                    ExtractXmpField(xmpText, "dc:subject",               _exifRows);
                    ExtractXmpField(xmpText, "xmpMM:DocumentID",         _exifRows);
                    if (_exifRows[^1].Tag == "--- XMP ---")
                        _exifRows.RemoveAt(_exifRows.Count - 1); // nothing extracted — drop header
                }
                catch { _exifRows.RemoveAt(_exifRows.Count - 1); }
            }

            // --- PNG custom eXIf chunk we injected ---
            if (isPng)
            {
                byte[]? chunk = ReadExifChunkFromPng(path);
                if (chunk is { Length: > 0 })
                {
                    _exifRows.Add(("--- PNG eXIf chunk ---", $"{chunk.Length} bytes (injected)"));
                    // Parse the TIFF header inside
                    ParseTiffDirectoryIntoRows(chunk, _exifRows);
                }
                else
                {
                    _exifRows.Add(("PNG eXIf chunk", "(none)"));
                }
            }

            if (_exifRows.Count == 0)
                _exifRows.Add(("(no metadata)", ""));
        }
        catch (Exception ex)
        {
            _exifError = $"Error reading EXIF: {ex.Message}";
        }
    }

    // ── Value formatting ──────────────────────────────────────────────────────

    private static string FormatExifValue(IExifValue val)
    {
        try
        {
            var obj = val.GetValue();
            return obj switch
            {
                null                => "(null)",
                byte[] bytes        => bytes.Length <= 64
                                        ? BitConverter.ToString(bytes).Replace("-", " ")
                                        : $"<{bytes.Length} bytes>",
                Array arr           => string.Join(", ", arr.Cast<object>().Select(o => o?.ToString() ?? "")),
                _                   => obj.ToString() ?? ""
            };
        }
        catch { return "(error)"; }
    }

    // ── XMP helper ────────────────────────────────────────────────────────────

    private static void ExtractXmpField(string xml, string fieldName,
        List<(string, string)> rows)
    {
        // Simple attribute form: fieldName="value"
        int idx = xml.IndexOf(fieldName + "=\"", StringComparison.Ordinal);
        if (idx >= 0)
        {
            int start = idx + fieldName.Length + 2;
            int end   = xml.IndexOf('"', start);
            if (end > start)
            {
                rows.Add((fieldName, xml[start..end]));
                return;
            }
        }
        // Element form: <fieldName>value</fieldName>
        idx = xml.IndexOf($"<{fieldName}>", StringComparison.Ordinal);
        if (idx >= 0)
        {
            int start = idx + fieldName.Length + 2;
            int end   = xml.IndexOf($"</{fieldName}>", start, StringComparison.Ordinal);
            if (end > start)
                rows.Add((fieldName, xml[start..end].Trim()));
        }
    }

    // ── PNG eXIf chunk reader (mirrors Processor.ReadExifChunkFromPng) ────────

    private static byte[]? ReadExifChunkFromPng(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            int pos = 8; // skip PNG signature
            while (pos + 12 <= data.Length)
            {
                int length = (int)(
                    ((uint)data[pos]     << 24) |
                    ((uint)data[pos + 1] << 16) |
                    ((uint)data[pos + 2] << 8)  |
                     (uint)data[pos + 3]);
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                if (type == "eXIf" && length > 0)
                    return data[(pos + 8)..(pos + 8 + length)];
                pos += 12 + length;
            }
        }
        catch { }
        return null;
    }

    // ── Minimal TIFF IFD parser ───────────────────────────────────────────────

    private static readonly Dictionary<ushort, string> KnownTags = new()
    {
        { 256,   "ImageWidth" },
        { 257,   "ImageLength" },
        { 271,   "Make" },
        { 272,   "Model" },
        { 274,   "Orientation" },
        { 282,   "XResolution" },
        { 283,   "YResolution" },
        { 296,   "ResolutionUnit" },
        { 305,   "Software" },
        { 306,   "DateTime" },
        { 315,   "Artist" },
        { 33434, "ExposureTime" },
        { 33437, "FNumber" },
        { 34850, "ExposureProgram" },
        { 34855, "ISOSpeedRatings" },
        { 36864, "ExifVersion" },
        { 36867, "DateTimeOriginal" },
        { 36868, "DateTimeDigitized" },
        { 37121, "ComponentsConfiguration" },
        { 37122, "CompressedBitsPerPixel" },
        { 37377, "ShutterSpeedValue" },
        { 37378, "ApertureValue" },
        { 37380, "ExposureBiasValue" },
        { 37381, "MaxApertureValue" },
        { 37383, "MeteringMode" },
        { 37384, "LightSource" },
        { 37385, "Flash" },
        { 37386, "FocalLength" },
        { 37500, "MakerNote" },
        { 37510, "UserComment" },
        { 40960, "FlashPixVersion" },
        { 40961, "ColorSpace" },
        { 40962, "PixelXDimension" },
        { 40963, "PixelYDimension" },
        { 41987, "WhiteBalance" },
        { 41989, "FocalLengthIn35mmFilm" },
        { 41990, "SceneCaptureType" },
    };

    private static void ParseTiffDirectoryIntoRows(byte[] tiff, List<(string, string)> rows)
    {
        try
        {
            if (tiff.Length < 8) return;

            bool le = tiff[0] == 'I' && tiff[1] == 'I'; // little-endian
            uint ifdOffset = ReadU32(tiff, 4, le);
            if (ifdOffset + 2 > (uint)tiff.Length) return;

            ushort entries = ReadU16(tiff, (int)ifdOffset, le);
            int pos = (int)ifdOffset + 2;

            for (int i = 0; i < entries && pos + 12 <= tiff.Length; i++, pos += 12)
            {
                ushort tag  = ReadU16(tiff, pos, le);
                ushort type = ReadU16(tiff, pos + 2, le);
                uint   count= ReadU32(tiff, pos + 4, le);
                string tagName = KnownTags.TryGetValue(tag, out var n) ? n : $"0x{tag:X4}";

                if (tagName.StartsWith("MakerNote") || tagName.StartsWith("0x")) continue;

                string value = ReadTiffValue(tiff, pos + 8, type, count, le, tiff);
                rows.Add(($"  {tagName}", value));
            }
        }
        catch { /* malformed TIFF — ignore */ }
    }

    private static string ReadTiffValue(byte[] tiff, int valueOffset, ushort type,
        uint count, bool le, byte[] full)
    {
        // For small values, data is inline (4 bytes); otherwise it's an offset
        uint byteCount = type switch
        {
            1 or 2 or 6 or 7 => count,
            3 or 8           => count * 2,
            4 or 9           => count * 4,
            5 or 10          => count * 8,
            11               => count * 4,
            12               => count * 8,
            _                => count
        };

        int dataPos = valueOffset;
        if (byteCount > 4)
        {
            uint offset = ReadU32(full, valueOffset, le);
            dataPos = (int)offset;
        }
        if (dataPos + byteCount > full.Length) return "(out of range)";

        return type switch
        {
            2  => // ASCII
                System.Text.Encoding.ASCII.GetString(full, dataPos, (int)count).TrimEnd('\0'),
            3  => // SHORT
                count == 1
                    ? ReadU16(full, dataPos, le).ToString()
                    : string.Join(", ", Enumerable.Range(0, (int)count)
                        .Select(i => ReadU16(full, dataPos + i * 2, le).ToString())),
            4  => // LONG
                ReadU32(full, dataPos, le).ToString(),
            5  => // RATIONAL
                count == 1
                    ? FormatRational(full, dataPos, le)
                    : string.Join(", ", Enumerable.Range(0, (int)count)
                        .Select(i => FormatRational(full, dataPos + i * 8, le))),
            _  => $"<type={type} count={count}>"
        };
    }

    private static string FormatRational(byte[] data, int pos, bool le)
    {
        uint num = ReadU32(data, pos, le);
        uint den = ReadU32(data, pos + 4, le);
        if (den == 0) return "0";
        return den == 1 ? num.ToString() : $"{num}/{den} ({(double)num / den:G5})";
    }

    private static ushort ReadU16(byte[] d, int pos, bool le)
        => le ? (ushort)(d[pos] | (d[pos + 1] << 8))
              : (ushort)((d[pos] << 8) | d[pos + 1]);

    private static uint ReadU32(byte[] d, int pos, bool le)
        => le ? (uint)(d[pos] | (d[pos+1]<<8) | (d[pos+2]<<16) | (d[pos+3]<<24))
              : (uint)((d[pos]<<24) | (d[pos+1]<<16) | (d[pos+2]<<8) | d[pos+3]);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void BrowseFolder()
    {
        var thread = new System.Threading.Thread(() =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to browse EXIF",
                UseDescriptionForTitle = true,
                AutoUpgradeEnabled = true
            };
            var current = ReadString(_folderBuf);
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _currentFolder = dialog.SelectedPath;
                WriteString(_folderBuf, _currentFolder);
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static string ReadString(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, len);
    }

    private static void WriteString(byte[] buffer, string value)
    {
        Array.Clear(buffer, 0, buffer.Length);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        int copyLen = Math.Min(bytes.Length, buffer.Length - 1);
        Array.Copy(bytes, buffer, copyLen);
    }
}
