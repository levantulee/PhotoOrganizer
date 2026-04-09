using ImGuiNET;
using PhotoOrganizer.Core;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace PhotoOrganizer.UI;

/// <summary>
/// Shows the full SQLite conversion history with pagination, status filtering, and text search.
/// </summary>
public sealed class DbHistoryPanel
{
    private const int PageSize = 500;

    private ConversionLog? _log;

    private List<ConversionLog.Entry> _page = new();
    private int  _totalCount   = 0;
    private int  _currentPage  = 0;
    private bool _loaded       = false;

    // Filters
    private int  _statusFilter = 0; // 0=All, 1=Success, 2=Failed, 3=Skipped
    private byte[] _textFilter = new byte[256];

    private static readonly string[] StatusLabels = { "All", "Success", "Failed", "Skipped" };
    private static readonly string?[] StatusValues = { null, "Success", "Failed", "Skipped" };

    public void SetLog(ConversionLog? log) => _log = log;

    public void Draw(ref bool show)
    {
        if (!show) return;

        ImGui.SetNextWindowSize(new SysVec2(1000, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("DB History", ref show)) { ImGui.End(); return; }

        // ── Toolbar ───────────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("##statusFilter", ref _statusFilter, StatusLabels, StatusLabels.Length))
            Reload();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("Search##dbsearch", _textFilter, (uint)_textFilter.Length))
        { /* filter is applied client-side */ }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            Reload();

        ImGui.SameLine();
        ImGui.Text($"Total: {_totalCount}");

        // ── Pagination ────────────────────────────────────────────────────────
        int totalPages = _totalCount == 0 ? 1 : (_totalCount + PageSize - 1) / PageSize;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int pageDisplay = _currentPage + 1;
        if (ImGui.InputInt("/ " + totalPages + " pages##page", ref pageDisplay, 0, 0))
        {
            _currentPage = Math.Clamp(pageDisplay - 1, 0, totalPages - 1);
            LoadPage();
        }
        ImGui.SameLine();
        if (ImGui.ArrowButton("##prev", ImGuiDir.Left) && _currentPage > 0)
        {
            _currentPage--;
            LoadPage();
        }
        ImGui.SameLine();
        if (ImGui.ArrowButton("##next", ImGuiDir.Right) && _currentPage < totalPages - 1)
        {
            _currentPage++;
            LoadPage();
        }

        ImGui.Separator();

        if (!_loaded) LoadPage();

        string textSearch = ReadStr(_textFilter).Trim();

        // ── Table ─────────────────────────────────────────────────────────────
        float tableHeight = ImGui.GetContentRegionAvail().Y - 4;
        if (ImGui.BeginTable("##dbhist", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
            new SysVec2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("ID",       ImGuiTableColumnFlags.WidthFixed,   55f);
            ImGui.TableSetupColumn("Time",     ImGuiTableColumnFlags.WidthFixed,  145f);
            ImGui.TableSetupColumn("Status",   ImGuiTableColumnFlags.WidthFixed,   60f);
            ImGui.TableSetupColumn("Source",   ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Output",   ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();

            foreach (var e in _page)
            {
                // Client-side text search
                if (textSearch.Length > 0 &&
                    !e.SourcePath.Contains(textSearch, StringComparison.OrdinalIgnoreCase) &&
                    !e.OutputPath.Contains(textSearch, StringComparison.OrdinalIgnoreCase) &&
                    !e.Status.Contains(textSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(e.Id.ToString());

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(e.ConvertedAt);

                ImGui.TableSetColumnIndex(2);
                var (color, label) = e.Status switch
                {
                    "Success" => (new SysVec4(0.4f, 1f, 0.4f, 1f), "OK"),
                    "Failed"  => (new SysVec4(1f, 0.3f, 0.3f, 1f), "FAIL"),
                    _         => (new SysVec4(1f, 0.8f, 0.2f, 1f), "SKIP"),
                };
                ImGui.TextColored(color, label);

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(e.SourcePath);

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(e.OutputPath);
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    private void Reload()
    {
        _currentPage = 0;
        _loaded = false;
    }

    private void LoadPage()
    {
        if (_log == null) { _loaded = true; return; }
        var sf = StatusValues[_statusFilter];
        _totalCount = _log.GetTotalCount();
        _page = _log.GetPage(_currentPage * PageSize, PageSize, sf);
        _loaded = true;
    }

    public void Invalidate() => _loaded = false;

    private static string ReadStr(byte[] buf)
    {
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return System.Text.Encoding.UTF8.GetString(buf, 0, len);
    }
}
