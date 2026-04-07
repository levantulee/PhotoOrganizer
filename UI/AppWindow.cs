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

    // UI state — folder paths
    private byte[] _sourceFolder = new byte[1024];
    private byte[] _exportFolder = new byte[1024];
    private byte[] _processedFolder = new byte[1024];
    private byte[] _failedFolder = new byte[1024];

    // Radio button: 0 = Year+Month, 1 = Year only
    private int _folderMode = 0;
    private bool _includeSubfolders = true;
    private static readonly int _maxCores = Environment.ProcessorCount;
    private int _coreCount = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));

    // Processing state
    private bool _isRunning = false;
    private CancellationTokenSource? _cts;
    private readonly RunStats _stats = new();
    private readonly List<(string text, ResultStatus status)> _log = new();
    private readonly object _logLock = new();
    private bool _autoScroll = true;

    public AppWindow()
    {
        StartupTimer.Log("AppWindow ctor done");
        SetDefaultPaths();
    }

    private void SetDefaultPaths()
    {
        WriteString(_sourceFolder, "");
        WriteString(_exportFolder, "");
        WriteString(_processedFolder, "");
        WriteString(_failedFolder, "");
    }

    protected override void OnLoad()
    {
        StartupTimer.Log("OnLoad() start");
        base.OnLoad();
        StartupTimer.Log("OnLoad() base done");

        _imGui = new ImGuiController(ClientWidth, ClientHeight);
        StartupTimer.Log("ImGuiController created");

        GL.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
        StartupTimer.Log("OnLoad() done — window visible, background init starting");

        AddLog("Initializing... (checking FFmpeg + GPU in background)", ResultStatus.Skipped);

        // Run slow startup ops off the UI thread so the window appears immediately
        Task.Run(async () =>
        {
            StartupTimer.Log("Background: OpenCL start");
            try
            {
                ImageMagick.OpenCL.IsEnabled = true;
                StartupTimer.Log("Background: OpenCL done");
                AddLog("OpenCL GPU acceleration enabled.", ResultStatus.Success);
            }
            catch
            {
                StartupTimer.Log("Background: OpenCL failed");
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
        _cts?.Cancel();
        _imGui.Dispose();
        base.OnUnload();
    }

    private void BuildUI()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);

        ImGui.Begin("PhotoOrganizer",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus);

        ImGui.TextColored(new SysVec4(0.4f, 0.8f, 1f, 1f), "PhotoOrganizer");
        ImGui.Separator();
        ImGui.Spacing();

        float labelCol = 130f;
        float browseWidth = 80f;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float inputWidth = ImGui.GetContentRegionAvail().X - labelCol - browseWidth - spacing * 2;

        PathRow("Source Folder:",    "##source",    _sourceFolder,    "Select Source Folder",    labelCol, inputWidth, browseWidth);
        ImGui.SetCursorPosX(labelCol);
        ImGui.Checkbox("Include subfolders", ref _includeSubfolders);
        PathRow("Export Folder:",    "##export",    _exportFolder,    "Select Export Folder",    labelCol, inputWidth, browseWidth);
        PathRow("Processed Folder:", "##processed", _processedFolder, "Select Processed Folder", labelCol, inputWidth, browseWidth);
        PathRow("Failed Folder:",    "##failed",    _failedFolder,    "Select Failed Folder",    labelCol, inputWidth, browseWidth);

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

        // Start / Stop
        bool canStart = !_isRunning && !string.IsNullOrWhiteSpace(ReadString(_sourceFolder));
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
        if (_isRunning)
        {
            ImGui.TextColored(new SysVec4(1f, 0.8f, 0.2f, 1f), "Running...");
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

        // Log window
        float logHeight = ImGui.GetContentRegionAvail().Y - 8;
        ImGui.BeginChild("##log", new SysVec2(0, logHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);

        lock (_logLock)
        {
            foreach (var (text, status) in _log)
            {
                SysVec4 color = status switch
                {
                    ResultStatus.Success => new SysVec4(0.4f, 1f, 0.4f, 1f),
                    ResultStatus.Skipped => new SysVec4(1f, 0.8f, 0.2f, 1f),
                    ResultStatus.Failed => new SysVec4(1f, 0.3f, 0.3f, 1f),
                    _ => new SysVec4(0.8f, 0.8f, 0.8f, 1f)
                };
                string prefix = status switch
                {
                    ResultStatus.Success => "[OK] ",
                    ResultStatus.Skipped => "[!!] ",
                    ResultStatus.Failed => "[XX] ",
                    _ => "     "
                };
                ImGui.TextColored(color, prefix + text);
            }
        }

        if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();
        ImGui.End();
    }

    private void StartProcessing()
    {
        var source = ReadString(_sourceFolder);
        var export = ReadString(_exportFolder);
        var processed = ReadString(_processedFolder);
        var failed = ReadString(_failedFolder);

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            AddLog("Source folder does not exist.", ResultStatus.Failed);
            return;
        }
        if (string.IsNullOrWhiteSpace(export))
        {
            AddLog("Export folder is required.", ResultStatus.Failed);
            return;
        }

        _isRunning = true;
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
            FolderMode = _folderMode == 0 ? FileOrganizer.FolderMode.YearMonth : FileOrganizer.FolderMode.YearOnly,
            IncludeSubfolders = _includeSubfolders,
            Parallelism = _coreCount
        };

        var processor = new Processor();
        processor.Progress += OnProgress;
        var ct = _cts.Token;

        Task.Run(async () =>
        {
            try
            {
                await processor.RunAsync(opts, ct);
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
            }
        }, ct);
    }

    private void StopProcessing()
    {
        _cts?.Cancel();
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

        var displayStatus = result.Entry.DateIsUnknown && result.Status == ResultStatus.Success
            ? ResultStatus.Skipped
            : result.Status;

        AddLog(prefix + result.Message, displayStatus);
    }

    private void AddLog(string text, ResultStatus status)
    {
        lock (_logLock) _log.Add((text, status));
    }

    private static void PathRow(string label, string id, byte[] buffer, string browseTitle,
        float labelCol, float inputWidth, float browseWidth)
    {
        ImGui.Text(label);
        ImGui.SameLine(labelCol);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText(id, buffer, (uint)buffer.Length);
        ImGui.SameLine();
        if (ImGui.Button($"Browse##{id}", new SysVec2(browseWidth, 0)))
            BrowseFolder(buffer, browseTitle);
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
