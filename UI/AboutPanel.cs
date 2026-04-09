using ImGuiNET;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace PhotoOrganizer.UI;

/// <summary>
/// About panel: app description, GitHub link, README and license text.
/// </summary>
public class AboutPanel
{
    private const string RepoUrl = "https://github.com/levantulee/LootboxSystem";

    private string _readmeText  = "";
    private string _licenseText = "";
    private bool   _loaded      = false;

    public void Draw(ref bool open)
    {
        if (!open) return;

        if (!_loaded)
            LoadContent();

        ImGui.SetNextWindowSize(new SysVec2(820, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("About PhotoOrganizer", ref open))
        {
            ImGui.End();
            return;
        }

        // ── Header ────────────────────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Text, new SysVec4(0.4f, 0.8f, 1f, 1f));
        ImGui.SetWindowFontScale(1.3f);
        ImGui.Text("PhotoOrganizer");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);
        ImGui.TextDisabled("by Simon Rozner");

        ImGui.Spacing();
        ImGui.TextWrapped("Automatically organizes photos and videos into a clean, date-based folder structure. Handles HEIC conversion, Google Takeout exports, EXIF/GPS preservation, and integrity verification.");
        ImGui.Spacing();

        ImGui.Text("Repository:");
        ImGui.SameLine();
        ImGui.TextColored(new SysVec4(0.4f, 0.8f, 1f, 1f), RepoUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton("Open##repo"))
            OpenUrl(RepoUrl);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Tabs ──────────────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("##aboutTabs"))
        {
            if (ImGui.BeginTabItem("README"))
            {
                DrawScrollableText("##readmeScroll", _readmeText);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("License"))
            {
                DrawScrollableText("##licenseScroll", _licenseText);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private static void DrawScrollableText(string id, string text)
    {
        float height = ImGui.GetContentRegionAvail().Y - 4f;
        ImGui.BeginChild(id, new SysVec2(0, height), ImGuiChildFlags.Border,
            ImGuiWindowFlags.HorizontalScrollbar);

        if (string.IsNullOrEmpty(text))
            ImGui.TextDisabled("(content not found)");
        else
            ImGui.TextUnformatted(text);

        ImGui.EndChild();
    }

    private void LoadContent()
    {
        _loaded = true;

        // Look for files next to the executable (copied by the build).
        string baseDir = AppContext.BaseDirectory;

        string readmePath  = Path.Combine(baseDir, "README.md");
        string licensePath = Path.Combine(baseDir, "LICENSE");

        _readmeText  = TryRead(readmePath)  ?? "(README.md not found)";
        _licenseText = TryRead(licensePath) ?? "(LICENSE not found)";
    }

    private static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore — browser launch failure is non-fatal */ }
    }
}
