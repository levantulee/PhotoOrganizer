using System.Drawing;
using System.Windows.Forms;

namespace PhotoOrganizer.UI;

/// <summary>
/// Lightweight WinForms splash — appears instantly before GLFW initialises.
/// Runs on its own STA thread so it stays responsive while the main thread
/// blocks inside GLFW.Init().
/// </summary>
internal sealed class SplashForm : Form
{
    private readonly Label _label;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(26, 26, 26);
        Size            = new Size(340, 120);
        TopMost         = true;

        _label = new Label
        {
            Text      = "PhotoOrganizer",
            ForeColor = Color.FromArgb(100, 200, 255),
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Fill
        };

        var sub = new Label
        {
            Text      = "Starting up…",
            ForeColor = Color.FromArgb(160, 160, 160),
            Font      = new Font("Segoe UI", 9f),
            AutoSize  = false,
            TextAlign = ContentAlignment.BottomCenter,
            Dock      = DockStyle.Bottom,
            Height    = 28
        };

        Controls.Add(_label);
        Controls.Add(sub);
    }

    /// <summary>Close the splash from any thread.</summary>
    public void CloseSplash()
    {
        if (IsHandleCreated)
            Invoke(Close);
    }
}
