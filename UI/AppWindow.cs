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
    private readonly VerificationPanel _verificationPanel = new();
    private readonly DbHistoryPanel _dbHistoryPanel = new();
    private readonly AboutPanel _aboutPanel = new();
    private readonly Action? _onReady;

    // Panel visibility
    private bool _showProcessing    = true;
    private bool _showHistory       = true;
    private bool _showExifViewer    = false;
    private bool _showVerification  = false;
    private bool _showDbHistory     = false;
    private bool _showAbout         = false;

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
    private bool _skipProcessed = true;
    private bool _videoPriority = true;
    private static readonly int _maxCores = Environment.ProcessorCount;
    private int _coreCount = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));

    // Dock layout
    private bool _dockLayoutInitialized = false;

    // Log search / selection
    private byte[] _logFilter = new byte[256];
    private int _selectedLogLine = -1;

    // Processing state
    private bool _isRunning = false;
    private bool _isPaused = false;
    private CancellationTokenSource? _cts;
    private Processor? _processor;
    private readonly RunStats _stats = new();
    private readonly List<(string text, ResultStatus? status)> _log = new();
    private readonly HashSet<string> _activeFiles = new(StringComparer.OrdinalIgnoreCase);
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
        _dbHistoryPanel.SetLog(_convLog);
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
            _skipProcessed     = s.SkipProcessed;
            _videoPriority     = s.VideoPriority;
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
                SkipProcessed      = _skipProcessed,
                VideoPriority      = _videoPriority,
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
        public bool    SkipProcessed      { get; set; } = true;
        public bool    VideoPriority      { get; set; } = true;
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

        AddLog("Initializing... (checking FFmpeg + GPU in background)", null);

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
                    AddLog("OpenCL not available — using CPU only.", null);
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
                ImGui.MenuItem("Processing",   null, ref _showProcessing);
                ImGui.MenuItem("History",      null, ref _showHistory);
                ImGui.MenuItem("EXIF Viewer",  null, ref _showExifViewer);
                ImGui.MenuItem("Verification", null, ref _showVerification);
                ImGui.MenuItem("DB History",   null, ref _showDbHistory);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("About"))
                    _showAbout = true;
                ImGui.EndMenu();
            }
            ImGui.EndMenuBar();
        }

        // ── DockSpace ─────────────────────────────────────────────────────────
        uint dockspaceId = ImGui.GetID("##MainDockspace");

        if (!_dockLayoutInitialized)
        {
            _dockLayoutInitialized = true;
            string iniPath = Path.Combine(AppContext.BaseDirectory, "imgui.ini");
            if (!File.Exists(iniPath))
                InitDefaultDockLayout(dockspaceId, viewport.WorkSize);
        }

        ImGui.DockSpace(dockspaceId, SysVec2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

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

        // ── Verification panel ────────────────────────────────────────────────
        _verificationPanel.Draw(ref _showVerification,
            ReadString(_sourceFolder), ReadString(_exportFolder),
            ReadString(_processedFolder), ReadString(_failedFolder));

        // ── DB History panel ──────────────────────────────────────────────────
        _dbHistoryPanel.Draw(ref _showDbHistory);

        // ── About panel ───────────────────────────────────────────────────────
        _aboutPanel.Draw(ref _showAbout);
    }

    private void BuildProcessingTab()
    {
        ImGui.Spacing();

        float labelCol = 130f;
        float browseWidth = 80f;
        float openWidth = 60f;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float inputWidth = ImGui.GetContentRegionAvail().X - labelCol - browseWidth - openWidth - spacing * 3;

        PathRow("Source Folder:",    "##source",    _sourceFolder,    "Select Source Folder",    labelCol, inputWidth, browseWidth, openWidth,
            required: true,  hint: "required");
        ImGui.SetCursorPosX(labelCol); ImGui.Checkbox("Include subfolders", ref _includeSubfolders);
        ImGui.SetCursorPosX(labelCol); ImGui.Checkbox("Infer missing dates from neighbours", ref _inferMissingDates);
        ImGui.SetCursorPosX(labelCol); ImGui.Checkbox("Skip already-processed files", ref _skipProcessed);
        ImGui.SetCursorPosX(labelCol);
        ImGui.Checkbox("Video priority mode", ref _videoPriority);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("ON: images pause while each video converts (full threads, one video at a time).\nOFF: videos share the thread pool with images (better throughput for video-heavy batches).");

        PathRow("Export Folder:",    "##export",    _exportFolder,    "Select Export Folder",    labelCol, inputWidth, browseWidth, openWidth,
            required: true,  hint: "required");
        PathRow("Failed Folder:",    "##failed",    _failedFolder,    "Select Failed Folder",    labelCol, inputWidth, browseWidth, openWidth,
            required: true,  hint: "required");
        PathRow("Processed Folder:", "##processed", _processedFolder, "Select Processed Folder", labelCol, inputWidth, browseWidth, openWidth,
            required: false, hint: "optional");

        ImGui.Spacing();

        // Parallel cores slider — adjustable before AND during a run
        ImGui.Text("Threads:");
        ImGui.SameLine(labelCol);
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("##cores", ref _coreCount, 1, _maxCores) && _isRunning)
            _processor?.SetParallelism(_coreCount);
        ImGui.SameLine();
        ImGui.TextDisabled(_isRunning ? $"(live {_coreCount} of {_maxCores} cores)" : $"(of {_maxCores} logical cores)");

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
        bool showActive = _isRunning;
        float lineH = ImGui.GetTextLineHeightWithSpacing();
        int activeMaxRows = showActive ? Math.Min(_coreCount, 6) : 0;
        float activeH = showActive ? lineH * (activeMaxRows + 1) + ImGui.GetStyle().WindowPadding.Y * 2 + ImGui.GetStyle().ItemSpacing.Y + 6 : 0f;
        float logHeight = ImGui.GetContentRegionAvail().Y - activeH - 4;
        ImGui.BeginChild("##log", new SysVec2(0, logHeight), ImGuiChildFlags.Border,
            ImGuiWindowFlags.HorizontalScrollbar);

        string filter = ReadString(_logFilter).Trim();

        lock (_logLock)
        {
            int idx = 0;
            foreach (var (text, status) in _log)
            {
                // null status = info/phase line — always shown, no prefix, grey
                bool isInfo = status == null;
                bool isExif = !isInfo && text.StartsWith("[EXIF", StringComparison.Ordinal);

                if (!isInfo && !isExif)
                {
                    if (status == ResultStatus.Success && !_filterOk)   { idx++; continue; }
                    if (status == ResultStatus.Skipped && !_filterSkip) { idx++; continue; }
                    if (status == ResultStatus.Failed  && !_filterErr)  { idx++; continue; }
                }
                if (isExif && !_filterExif) { idx++; continue; }

                string prefix = isInfo  ? "--- " :
                                isExif  ? "[dbg] " :
                                status switch
                                {
                                    ResultStatus.Success => "[OK]  ",
                                    ResultStatus.Skipped => "[!!]  ",
                                    ResultStatus.Failed  => "[XX]  ",
                                    _                    => "      "
                                };
                string line = prefix + text;

                if (filter.Length > 0 &&
                    !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                    continue;
                }

                var grey    = new SysVec4(0.5f, 0.5f, 0.5f, 1f);
                SysVec4 color = isInfo || isExif ? grey : status switch
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

        // ── Active files section ──────────────────────────────────────────────
        if (showActive)
        {
            string[] active;
            lock (_logLock)
                active = _activeFiles.Count > 0
                    ? _activeFiles.Select(p => Path.GetFileName(p)!).ToArray()
                    : Array.Empty<string>();

            ImGui.Separator();
            ImGui.BeginChild("##activefiles", new SysVec2(0, activeH - ImGui.GetStyle().ItemSpacing.Y - 3), ImGuiChildFlags.None);
            ImGui.TextDisabled($"Converting ({active.Length}):");
            foreach (var name in active.Take(activeMaxRows))
                ImGui.TextColored(new SysVec4(0.5f, 0.85f, 1f, 1f), $"  \u25B6 {name}");
            ImGui.EndChild();
        }
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

        var alreadyProcessed = _skipProcessed
            ? _convLog?.GetProcessedSourcePaths()
            : null;
        if (alreadyProcessed?.Count > 0)
            AddLog($"Skip-duplicates: {alreadyProcessed.Count} previously processed files on record.", null);

        var opts = new ProcessorOptions
        {
            SourceFolder      = source,
            ExportFolder      = export,
            ProcessedFolder   = processed,
            FailedFolder      = failed,
            FolderMode        = _folderMode == 0 ? FileOrganizer.FolderMode.YearMonth : FileOrganizer.FolderMode.YearOnly,
            IncludeSubfolders = _includeSubfolders,
            InferMissingDates = _inferMissingDates,
            VideoPriority     = _videoPriority,
            AlreadyProcessed  = alreadyProcessed,
            Parallelism       = _coreCount
        };

        _processor = new Processor();
        _processor.Progress += OnProgress;
        _processor.FileStarted += path => { lock (_logLock) _activeFiles.Add(path); };
        lock (_logLock) _activeFiles.Clear();
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
                AddLog("Stopped by user.", null);
            }
            catch (Exception ex)
            {
                AddLog($"Fatal error: {ex.Message}", ResultStatus.Failed);
            }
            finally
            {
                _isRunning = false;
                _isPaused = false;
                lock (_logLock) _activeFiles.Clear();
            }
        }, ct);
    }

    private void StopProcessing()
    {
        // Open the gate first so paused tasks can observe cancellation immediately
        _processor?.Resume();
        _isPaused  = false;
        _isRunning = false;  // immediate UI feedback; the background task cleans up the rest
        _cts?.Cancel();
    }

    private void PauseProcessing()
    {
        _isPaused = true;
        _processor?.Pause();
        AddLog("Paused — active conversions will finish.", null);
    }

    private void ResumeProcessing()
    {
        _isPaused = false;
        _processor?.Resume();
        AddLog("Resumed.", null);
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

        if (string.IsNullOrWhiteSpace(failed))
            errors.Add("Failed folder is required.");
        else if (!Directory.Exists(failed))
            errors.Add($"Failed folder not found: {failed}");

        if (!string.IsNullOrWhiteSpace(processed) && !Directory.Exists(processed))
            errors.Add($"Processed folder path not found: {processed}");

        return errors.Count == 0;
    }

    private void OnProgress(ProcessResult result)
    {
        // Info events (no source path) are phase announcements — neutral, no stats, no DB.
        if (string.IsNullOrEmpty(result.Entry.SourcePath))
        {
            AddLog(result.Message, null);
            return;
        }

        // EXIF diagnostic events are debug-only — don't touch stats or DB,
        // just park them in the log under the EXIF filter.
        if (result.Message.StartsWith("[EXIF", StringComparison.Ordinal))
        {
            AddLog(result.Message, ResultStatus.Skipped); // Skipped kept so EXIF filter still works
            return;
        }

        lock (_logLock)
        {
            _activeFiles.Remove(result.Entry.SourcePath);
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

        // Log real file outcomes to history DB.
        // Exclude: EXIF diagnostic events (internal, no real output path),
        //          already-processed skips (already in DB with Success status).
        bool isExifDiag       = result.Message.StartsWith("[EXIF", StringComparison.Ordinal);
        bool isAlreadySkipped = result.Message.StartsWith("Already processed:", StringComparison.Ordinal);
        bool hasSourcePath    = !string.IsNullOrEmpty(result.Entry.SourcePath);

        if (hasSourcePath && !isExifDiag && !isAlreadySkipped &&
            result.Status is ResultStatus.Success or ResultStatus.Failed or ResultStatus.Skipped)
        {
            _convLog?.Log(result.Entry.SourcePath, result.OutputPath,
                          result.Entry.Category, result.Status);
            _historyLoaded = false;
            _dbHistoryPanel.Invalidate();
        }
    }

    private const int MaxLogEntries = 20_000;

    private void AddLog(string text, ResultStatus? status)
    {
        lock (_logLock)
        {
            if (_log.Count >= MaxLogEntries)
                _log.RemoveRange(0, MaxLogEntries / 4); // drop oldest 25 %
            _log.Add((text, status));
        }
    }

    private static void PathRow(string label, string id, byte[] buffer, string browseTitle,
        float labelCol, float inputWidth, float browseWidth, float openWidth,
        bool required, string hint = "")
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
        string inputStr = current;
        if (ImGui.InputTextWithHint(id, hint, ref inputStr, (uint)buffer.Length))
            WriteString(buffer, inputStr);
        ImGui.SameLine();
        if (ImGui.Button($"Browse##{id}", new SysVec2(browseWidth, 0)))
            BrowseFolder(buffer, browseTitle);

        if (highlight)
            ImGui.PopStyleColor();

        ImGui.SameLine();
        bool canOpen = !isEmpty && Directory.Exists(current);
        if (!canOpen) ImGui.BeginDisabled();
        if (ImGui.Button($"Open##{id}", new SysVec2(openWidth, 0)))
            OpenFolder(current);
        if (!canOpen) ImGui.EndDisabled();
    }

    // ── DockBuilder P/Invoke — not exposed by ImGui.NET wrapper ─────────────
    [System.Runtime.InteropServices.DllImport("cimgui",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igDockBuilderRemoveNode(uint nodeId);

    [System.Runtime.InteropServices.DllImport("cimgui",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern uint igDockBuilderAddNode(uint nodeId, int flags);

    [System.Runtime.InteropServices.DllImport("cimgui",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodeSize(uint nodeId, SysVec2 size);

    [System.Runtime.InteropServices.DllImport("cimgui",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern unsafe void igDockBuilderSplitNode(uint nodeId, ImGuiDir splitDir,
        float sizeRatio, uint* outAtDir, uint* outOpposite);

    [System.Runtime.InteropServices.DllImport("cimgui",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern unsafe void igDockBuilderDockWindow(byte* windowName, uint nodeId);

    [System.Runtime.InteropServices.DllImport("cimgui",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igDockBuilderFinish(uint nodeId);

    private static unsafe void DockBuilderDockWindow(string name, uint nodeId)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* ptr = bytes)
            igDockBuilderDockWindow(ptr, nodeId);
    }

    private static unsafe void InitDefaultDockLayout(uint dockspaceId, SysVec2 size)
    {
        igDockBuilderRemoveNode(dockspaceId);
        igDockBuilderAddNode(dockspaceId, 0);
        igDockBuilderSetNodeSize(dockspaceId, size);

        // Carve a bottom strip for Verification (~28% height)
        uint nodeBottom, nodeTop;
        igDockBuilderSplitNode(dockspaceId, ImGuiDir.Down, 0.28f, &nodeBottom, &nodeTop);

        // Split the top area: Processing on left (~55%), History+DBHistory on right
        uint nodeLeft, nodeRight;
        igDockBuilderSplitNode(nodeTop, ImGuiDir.Right, 0.45f, &nodeRight, &nodeLeft);

        DockBuilderDockWindow("Processing",  nodeLeft);
        DockBuilderDockWindow("History",     nodeRight);
        DockBuilderDockWindow("DB History",  nodeRight);  // tabs with History
        DockBuilderDockWindow("Verification", nodeBottom);

        igDockBuilderFinish(dockspaceId);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
                { UseShellExecute = true });
        }
        catch { /* non-fatal */ }
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
