using PhotoOrganizer.UI;
using System.Windows.Forms;

namespace PhotoOrganizer;

static class Program
{
    [STAThread]
    static void Main()
    {
        StartupTimer.Log("Main() entered");

        // Show splash on a background STA thread — purely cosmetic, no GLFW to wait for
        SplashForm? splash = null;
        var splashReady = new ManualResetEventSlim(false);

        var splashThread = new Thread(() =>
        {
            splash = new SplashForm();
            splash.Shown += (_, _) => splashReady.Set();
            Application.Run(splash);
        });
        splashThread.SetApartmentState(ApartmentState.STA);
        splashThread.IsBackground = true;
        splashThread.Start();

        splashReady.Wait();
        StartupTimer.Log("Splash visible");

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var msg = e.ExceptionObject?.ToString() ?? "Unknown error";
            StartupTimer.Log($"UnhandledException: {msg}");
            splash?.CloseSplash();
            MessageBox.Show(msg, "Unhandled Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            StartupTimer.Log("new AppWindow()...");
            // Pass the splash-close callback so the window dismisses it after OnLoad(),
            // i.e. once the GL context + ImGui are ready and the first frame is about to render.
            using var window = new AppWindow(onReady: () => splash?.CloseSplash());

            StartupTimer.Log("Starting window.Run()...");
            window.Run();
            StartupTimer.Log("window.Run() returned");
        }
        catch (Exception ex)
        {
            StartupTimer.LogException("Main", ex);
            splash?.CloseSplash();
            MessageBox.Show(
                $"{ex.GetType().Name}:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
