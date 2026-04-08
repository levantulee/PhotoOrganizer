using System.Text.Json;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using PhotoOrganizer.Core;
using PhotoOrganizer.Models;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace PhotoOrganizer.UI;

public class AppWindow : Win32GameWindow
{
    private ImGuiController _imGui = null!;
    private ConversionLog? _convLog;
    private readonly ExifViewerPanel _exifViewer = new();
    private readonly Action? _onReady;

    // Panel visibility
    private bool _showProcessing  = true;
    private bool _showHistory     = true;
    private bool _showExifViewer  = false;

    // History tab state
    private List<ConversionLog.Entry> _history = new();
    private bool _historyLoaded = false;

    // UI state — folder paths
    private byte[] _sourceFolder = new byte[1024];
    private byte[] _exportFolder = new byte[1024];
    private byte[] _processedFolder = new byte[1024];
    private byte[] _failedFolder = new byte[1024];

    // Radio button: 0 = Year+Month, 1 = Year only
    private int _folderMode = 0;
    private bool _includeSubfolders = true;
    private bool _inferMissingDates = true;
    private static readonly int _maxCores = Environment.ProcessorCount;
    private int _coreCount = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));

    // Log search / selection
    private byte[] _logFilter = new byte[256];
    private int _selectedLogLine = -1;

    // Processing state
    private bool _isRunning = false;
    private bool _isPaused = false;
    private CancellationTokenSource? _cts;
    private Processor? _processor;
    private readonly RunStats _stats = new();
    private readonly List<(string text, ResultStatus status)> _log = new();
    private readonly object _logLock = new();
    private bool _autoScroll = true;

    // Log filters
    private bool _filterOk   = true;
    private bool _filterSkip = true;
    private bool _filterErr  = true;
    private bool _filterExif = false; // EXIF diag lines hidden by default

    public AppWindow(Action? onReady = null)
    {
        _onReady = onReady;
        StartupTimer.Log("AppWindow ctor done");
        try { _convLog = new ConversionLog(); } catch { /* DB unavailable */ }
        LoadSettings();
    }

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "PhotoOrganizer", "settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s == null) return;
            WriteString(_sourceFolder,    s.SourceFolder    ?? "");
            WriteString(_exportFolder,    s.ExportFolder    ?? "");
            WriteString(_processedFolder, s.ProcessedFolder ?? "");
            WriteString(_failedFolder,    s.FailedFolder    ?? "");
            _folderMode        = s.FolderMode;
            _includeSubfolders = s.IncludeSubfolders;
            _inferMissingDates = s.InferMissingDates;
            _coreCount         = Math.Max(1, Math.Min(_maxCores, s.CoreCount));
        }
        catch { /* ignore corrupt settings */ }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var s = new AppSettings
            {
                SourceFolder    = ReadString(_sourceFolder),
                ExportFolder    = ReadString(_exportFolder),
                ProcessedFolder = ReadString(_processedFolder),
                FailedFolder    = ReadString(_failedFolder),
                FolderMode      = _folderMode,
                IncludeSubfolders  = _includeSubfolders,
                InferMissingDates  = _inferMissingDates,
                CoreCount          = _coreCount
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { /* ignore write failures */ }
    }

    private sealed class AppSettings
    {
        public string? SourceFolder    { get; set; }
        public string? ExportFolder    { get; set; }
        public string? ProcessedFolder { get; set; }
        public string? FailedFolder    { get; set; }
        public int     FolderMode      { get; set; }
        public bool    IncludeSubfolders  { get; set; } = true;
        public bool    InferMissingDates  { get; set; } = true;
        public int     CoreCount          { get; set; } = 1;
    }

    protected override void OnLoad()
    {
        StartupTimer.Log("OnLoad() start");
        base.OnLoad();
        StartupTimer.Log("OnLoad() base done");

        _imGui = new ImGuiController(ClientWidth, ClientHeight);
        StartupTimer.Log("ImGuiController created");

        GL.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);

        // Dismiss the splash now — GL context + ImGui are ready, window is about to render
        _onReady?.Invoke();
        StartupTimer.Log("OnLoad() done — window visible, background init starting");

        AddLog("Initializing... (checking FFmpeg + GPU in background)", ResultStatus.Skipped);

        // Run slow startup ops off the UI thread so the window appears immediately
        Task.Run(async () =>
        {
            try
            {
                StartupTimer.Log("Background: OpenCL start");
                try
                {
                    ImageMagick.OpenCL.IsEnabled = true;
                    StartupTimer.Log("Background: OpenCL done");
                    AddLog("OpenCL GPU acceleration enabled.", ResultStatus.Success);
                }
                catch (Exception ex)
                {
                    StartupTimer.Log($"Background: OpenCL failed — {ex.Message}");
                    AddLog("OpenCL not available — using CPU only.", ResultStatus.Skipped);
                }

                StartupTimer.Log("Background: FFmpeg check start");
                bool ffmpeg = await VideoConverter.CheckFfmpegAsync();
                StartupTimer.Log($"Background: FFmpeg check done — found={ffmpeg}");
                AddLog(ffmpeg
                    ? "FFmpeg found. Video conversion enabled."
                    : "FFmpeg not found — video conversion disabled. Install FFmpeg and add to PATH.",
                    ffmpeg ? ResultStatus.Success : ResultStatus.Failed);

                StartupTimer.Log("Background: all init complete");
            }
            catch (Exception ex)
            {
                StartupTimer.Log($"Background: unhandled error — {ex}");
                AddLog($"Init error: {ex.Message}", ResultStatus.Failed);
            }
        });
    }

    protected override void OnResize(int width, int height)
    {
        base.OnResize(width, height);
        if (_imGui == null) return; // GL not loaded yet — WM_SIZE fires during CreateWindowEx
        GL.Viewport(0, 0, width, height);
        _imGui.WindowResized(width, height);
    }

    protected override void OnUpdate(double deltaTime)
    {
        base.OnUpdate(deltaTime);
        _imGui.Update(this, (float)deltaTime);
        BuildUI();
    }

    protected override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _imGui.Render();
        SwapBuffers();
    }

    protected override void OnUnload()
    {
        SaveSettings();
        _cts?.Cancel();
        _convLog?.Dispose();
        _imGui.Dispose();
        base.OnUnload();
    }

    private void BuildUI()
    {
        // ── Full-screen dockspace host ─────────────────────────────────────────
        // This invisible window owns the menu bar and provides the DockSpace that
        // all child panels dock into. It never draws decorations itself.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    SysVec2.Zero);

        const ImGuiWindowFlags hostFlags =
            ImGuiWindowFlags.NoTitleBar         | ImGuiWindowFlags.NoCollapse   |
            ImGuiWindowFlags.NoResize           | ImGuiWindowFlags.NoMove       |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoBackground       | ImGuiWindowFlags.NoDocking    |
            ImGuiWindowFlags.MenuBar;

        bool hostOpen = true;
        ImGui.Begin("##DockHost", ref hostOpen, hostFlags);
        ImGui.PopStyleVar(3);

        // ── Menu bar inside host ──────────────────────────────────────────────
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Processing",  null, ref _showProcessing);
                ImGui.MenuItem("History",     null, ref _showHistory);
                ImGui.MenuItem("EXIF Viewer", null, ref _showExifViewer);
                ImGui.EndMenu();
            }
            ImGui.EndMenuBar();
        }

        // ── DockSpace ─────────────────────────────────────────────────────────
        ImGui.DockSpace(ImGui.GetID("##MainDockspace"), SysVec2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        ImGui.End(); // host

        // ── Processing panel ──────────────────────────────────────────────────
        if (_showProcessing)
        {
            ImGui.SetNextWindowSize(
                new SysVec2(viewport.WorkSize.X * 0.55f, viewport.WorkSize.Y),
                ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Processing", ref _showProcessing))
                BuildProcessingTab();
            ImGui.End();
        }

        // ── History panel ─────────────────────────────────────────────────────
        if (_showHistory)
        {
            ImGui.SetNextWindowSize(
                new SysVec2(viewport.WorkSize.X * 0.45f, viewport.WorkSize.Y),
                ImGuiCond.FirstUseEver);
            bool prevShow = _showHistory;
            if (ImGui.Begin("History", ref _showHistory))
                BuildHistoryTab();
            ImGui.End();

            if (prevShow && !_showHistory)
                _historyLoaded = false;
        }
        else
        {
            _historyLoaded = false;
        }

        // ── EXIF Viewer panel ─────────────────────────────────────────────────
        _exifViewer.Draw(ref _showExifViewer);
    }

    private void BuildProcessingTab()
    {
        ImGui.Spacing();

        float labelCol = 130f;
        float browseWidth = 80f;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float inputWidth = ImGui.GetContentRegionAvail().X - labelCol - browseWidth - spacing * 2;

        PathRow("Source Folder:",    "##source",    _sourceFolder,    "Select Source Folder",    labelCol, inputWidth, browseWidth,
            required: true);
        ImGui.SetCursorPosX(labelCol);
        ImGui.Checkbox("Include subfolders", ref _includeSubfolders);
        ImGui.SameLine();
        ImGui.Checkbox("Infer missing dates from neighbours", ref _inferMissingDates);
        PathRow("Export Folder:",    "##export",    _exportFolder,    "Select Export Folder",    labelCol, inputWidth, browseWidth,
            required: true);
        PathRow("Processed Folder:", "##processed", _processedFolder, "Select Processed Folder", labelCol, inputWidth, browseWidth,
            required: false);
        PathRow("Failed Folder:",    "##failed",    _failedFolder,    "Select Failed Folder",    labelCol, inputWidth, browseWidth,
            required: false);

        ImGui.Spacing();

        // Parallel cores slider
        ImGui.Text("Threads:");
        ImGui.SameLine(labelCol);
        ImGui.SetNextItemWidth(200);
        ImGui.SliderInt("##cores", ref _coreCount, 1, _maxCores);
        ImGui.SameLine();
        ImGui.TextDisabled($"(of {_maxCores} logical cores)");

        ImGui.Spacing();

        // Folder structure radio buttons
        ImGui.Text("Folder structure:");
        ImGui.SameLine();
        ImGui.RadioButton("Year + Month", ref _folderMode, 0);
        ImGui.SameLine();
        ImGui.RadioButton("Year only", ref _folderMode, 1);

        ImGui.Spacing();

        // Start / Stop / Pause / Resume
        bool canStart = !_isRunning && ValidateFolders(out _);
        if (!canStart) ImGui.BeginDisabled();
        if (ImGui.Button("  Start  "))
            StartProcessing();
        if (!canStart) ImGui.EndDisabled();

        ImGui.SameLine();

        bool canStop = _isRunning;
        if (!canStop) ImGui.BeginDisabled();
        if (ImGui.Button("  Stop  "))
            StopProcessing();
        if (!canStop) ImGui.EndDisabled();

        ImGui.SameLine();

        if (_isRunning && !_isPaused)
        {
            if (ImGui.Button("  Pause  "))
                PauseProcessing();
            ImGui.SameLine();
            ImGui.TextColored(new SysVec4(1f, 0.8f, 0.2f, 1f), "Running...");
        }
        else if (_isRunning && _isPaused)
        {
            if (ImGui.Button("  Resume  "))
                ResumeProcessing();
            ImGui.SameLine();
            ImGui.TextColored(new SysVec4(1f, 0.6f, 0.1f, 1f), "Paused");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Stats bar
        RunStats stats;
        lock (_logLock) { stats = new RunStats { Photos = _stats.Photos, Videos = _stats.Videos, Heic = _stats.Heic, Skipped = _stats.Skipped, Errors = _stats.Errors }; }

        ImGui.Text($"Photos: {stats.Photos}");
        ImGui.SameLine();
        ImGui.Text($"Videos: {stats.Videos}");
        ImGui.SameLine();
        ImGui.Text($"HEIC: {stats.Heic}");
        ImGui.SameLine();
        ImGui.Text($"Skipped: {stats.Skipped}");
        ImGui.SameLine();
        ImGui.TextColored(stats.Errors > 0 ? new SysVec4(1f, 0.3f, 0.3f, 1f) : new SysVec4(0.7f, 0.7f, 0.7f, 1f),
            $"Errors: {stats.Errors}");

        ImGui.Separator();

        // ── Log filter bar ────────────────────────────────────────────────────
        // Level toggles
        ImGui.Checkbox("OK", ref _filterOk);
        ImGui.SameLine();
        ImGui.Checkbox("Skip", ref _filterSkip);
        ImGui.SameLine();
        ImGui.Checkbox("Error", ref _filterErr);
        ImGui.SameLine();
        ImGui.Checkbox("EXIF", ref _filterExif);
        ImGui.SameLine();
        ImGui.Separator();
        ImGui.SameLine();

        // Text search
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 130f);
        ImGui.InputText("##logfilter", _logFilter, (uint)_logFilter.Length);
        ImGui.SameLine();
        if (ImGui.Button("Clear##filter"))
        {
            Array.Clear(_logFilter, 0, _logFilter.Length);
            _selectedLogLine = -1;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);

        // ── Log lines ─────────────────────────────────────────────────────────
        float logHeight = ImGui.GetContentRegionAvail().Y - 4;
        ImGui.BeginChild("##log", new SysVec2(0, logHeight), ImGuiChildFlags.Border,
            ImGuiWindowFlags.HorizontalScrollbar);

        string filter = ReadString(_logFilter).Trim();

        lock (_logLock)
        {
            int idx = 0;
            foreach (var (text, status) in _log)
            {
                // Level filter
                bool isExif = text.StartsWith("[EXIF", StringComparison.Ordinal);
                if (isExif && !_filterExif) { idx++; continue; }
                if (!isExif)
                {
                    if (status == ResultStatus.Success && !_filterOk)   { idx++; continue; }
                    if (status == ResultStatus.Skipped && !_filterSkip) { idx++; continue; }
                    if (status == ResultStatus.Failed  && !_filterErr)  { idx++; continue; }
                }

                string prefix = status switch
                {
                    ResultStatus.Success => "[OK] ",
                    ResultStatus.Skipped => "[!!] ",
                    ResultStatus.Failed  => "[XX] ",
                    _                   => "     "
                };
                string line = prefix + text;

                if (filter.Length > 0 &&
                    !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                    continue;
                }

                SysVec4 color = status switch
                {
                    ResultStatus.Success => new SysVec4(0.4f, 1f, 0.4f, 1f),
                    ResultStatus.Skipped => new SysVec4(1f, 0.8f, 0.2f, 1f),
                    ResultStatus.Failed  => new SysVec4(1f, 0.3f, 0.3f, 1f),
                    _                   => new SysVec4(0.8f, 0.8f, 0.8f, 1f)
                };

                ImGui.PushID(idx);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable(line, _selectedLogLine == idx,
                    ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedLogLine = idx;
                    ImGui.SetClipboardText(line);
                }
                ImGui.PopStyleColor();
                ImGui.PopID();

                idx++;
            }
        }

        if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();
    }

    private void BuildHistoryTab()
    {
        if (!_historyLoaded)
        {
            _history = _convLog?.GetRecent() ?? new();
            _historyLoaded = true;
        }

        ImGui.Spacing();
        ImGui.Text($"{_history.Count} entries");
        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            _history = _convLog?.GetRecent() ?? new();
        }
        ImGui.SameLine();
        if (ImGui.Button("Purge All"))
        {
            _convLog?.Purge();
            _history = new();
        }
        ImGui.Separator();

        float tableHeight = ImGui.GetContentRegionAvail().Y - 4;
        if (ImGui.BeginTable("##history", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
            new SysVec2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Time",   ImGuiTableColumnFlags.WidthFixed,   140f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed,    50f);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Output", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();

            foreach (var e in _history)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(e.ConvertedAt);

                ImGui.TableSetColumnIndex(1);
                var (color, label) = e.Status switch
                {
                    "Success" => (new SysVec4(0.4f, 1f, 0.4f, 1f), "OK"),
                    "Failed"  => (new SysVec4(1f, 0.3f, 0.3f, 1f), "FAIL"),
                    _         => (new SysVec4(1f, 0.8f, 0.2f, 1f), "SKIP"),
                };
                ImGui.TextColored(color, label);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(Path.GetFileName(e.SourcePath));

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(Path.GetFileName(e.OutputPath));
            }

            ImGui.EndTable();
        }
    }

    private void StartProcessing()
    {
        var source = ReadString(_sourceFolder);
        var export = ReadString(_exportFolder);
        var processed = ReadString(_processedFolder);
        var failed = ReadString(_failedFolder);

        if (!ValidateFolders(out var errors))
        {
            foreach (var e in errors)
                AddLog(e, ResultStatus.Failed);
            return;
        }

        _isRunning = true;
        _isPaused = false;
        _cts = new CancellationTokenSource();
        _stats.Reset();
        lock (_logLock) _log.Clear();
        AddLog($"Starting... Source: {source}", ResultStatus.Success);

        var opts = new ProcessorOptions
        {
            SourceFolder = source,
            ExportFolder = export,
            ProcessedFolder = processed,
            FailedFolder = failed,
            FolderMode        = _folderMode == 0 ? FileOrganizer.FolderMode.YearMonth : FileOrganizer.FolderMode.YearOnly,
            IncludeSubfolders = _includeSubfolders,
            InferMissingDates = _inferMissingDates,
            Parallelism       = _coreCount
        };

        _processor = new Processor();
        _processor.Progress += OnProgress;
        var ct = _cts.Token;

        Task.Run(async () =>
        {
            try
            {
                await _processor.RunAsync(opts, ct);
                AddLog("Done!", ResultStatus.Success);
            }
            catch (OperationCanceledException)
            {
                AddLog("Stopped by user.", ResultStatus.Skipped);
            }
            catch (Exception ex)
            {
                AddLog($"Fatal error: {ex.Message}", ResultStatus.Failed);
            }
            finally
            {
                _isRunning = false;
                _isPaused = false;
            }
        }, ct);
    }

    private void StopProcessing()
    {
        // If paused, open the gate first so blocked tasks can observe cancellation
        _processor?.Resume();
        _isPaused = false;
        _cts?.Cancel();
    }

    private void PauseProcessing()
    {
        _isPaused = true;
        _processor?.Pause();
        AddLog("Paused — active conversions will finish.", ResultStatus.Skipped);
    }

    private void ResumeProcessing()
    {
        _isPaused = false;
        _processor?.Resume();
        AddLog("Resumed.", ResultStatus.Skipped);
    }

    /// <summary>
    /// Validates all configured folder paths.
    /// Source must be set and exist.
    /// Export must be set (created at runtime, no existence check).
    /// Processed / Failed: optional — if set, must exist.
    /// </summary>
    private bool ValidateFolders(out List<string> errors)
    {
        errors = new();
        var source    = ReadString(_sourceFolder);
        var export    = ReadString(_exportFolder);
        var processed = ReadString(_processedFolder);
        var failed    = ReadString(_failedFolder);

        if (string.IsNullOrWhiteSpace(source))
            errors.Add("Source folder is required.");
        else if (!Directory.Exists(source))
            errors.Add($"Source folder not found: {source}");

        if (string.IsNullOrWhiteSpace(export))
            errors.Add("Export folder is required.");

        if (!string.IsNullOrWhiteSpace(processed) && !Directory.Exists(processed))
            errors.Add($"Processed folder path not found: {processed}");

        if (!string.IsNullOrWhiteSpace(failed) && !Directory.Exists(failed))
            errors.Add($"Failed folder path not found: {failed}");

        return errors.Count == 0;
    }

    private void OnProgress(ProcessResult result)
    {
        lock (_logLock)
        {
            switch (result.Status)
            {
                case ResultStatus.Success:
                    switch (result.Entry.Category)
                    {
                        case Models.FileCategory.Video: _stats.Videos++; break;
                        case Models.FileCategory.Heic: _stats.Heic++; break;
                        default: _stats.Photos++; break;
                    }
                    break;
                case ResultStatus.Skipped:
                    _stats.Skipped++;
                    break;
                case ResultStatus.Failed:
                    _stats.Errors++;
                    break;
            }
        }

        string prefix = result.Status switch
        {
            ResultStatus.Success => result.Entry.DateIsUnknown ? "no date: " : "",
            ResultStatus.Skipped => "skip: ",
            ResultStatus.Failed => "ERR: ",
            _ => ""
        };

        var displayStatus = result.Status == ResultStatus.Success &&
                            (result.Entry.DateIsUnknown || result.Entry.DateIsEstimated)
            ? ResultStatus.Skipped   // yellow — date was missing or estimated
            : result.Status;

        AddLog(prefix + result.Message, displayStatus);

        if (result.Status is ResultStatus.Success or ResultStatus.Failed)
        {
            _convLog?.Log(result.Entry.SourcePath, result.OutputPath,
                          result.Entry.Category, result.Status);
            _historyLoaded = false;
        }
    }

    private void AddLog(string text, ResultStatus status)
    {
        lock (_logLock) _log.Add((text, status));
    }

    private static void PathRow(string label, string id, byte[] buffer, string browseTitle,
        float labelCol, float inputWidth, float browseWidth, bool required)
    {
        string current = ReadString(buffer);
        bool isEmpty   = string.IsNullOrWhiteSpace(current);
        bool invalid   = !isEmpty && !Directory.Exists(current);
        bool missingRequired = required && isEmpty;

        // Red tint when path is set but doesn't exist, or required but empty
        bool highlight = invalid || missingRequired;
        if (highlight)
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new SysVec4(0.4f, 0.1f, 0.1f, 1f));

        ImGui.Text(label);
        ImGui.SameLine(labelCol);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText(id, buffer, (uint)buffer.Length);
        ImGui.SameLine();
        if (ImGui.Button($"Browse##{id}", new SysVec2(browseWidth, 0)))
            BrowseFolder(buffer, browseTitle);

        if (highlight)
            ImGui.PopStyleColor();
    }

    private static void BrowseFolder(byte[] buffer, string title)
    {
        // Must run on STA thread
        var thread = new System.Threading.Thread(() =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                AutoUpgradeEnabled = true  // modern Windows Explorer-style picker
            };

            // Pre-populate with current value
            var current = ReadString(buffer);
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                WriteString(buffer, dialog.SelectedPath);
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
