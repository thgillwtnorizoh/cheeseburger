namespace Cheeseburger.DbStudio;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash("Unhandled application exception", e.ExceptionObject as Exception
                ?? new Exception(Convert.ToString(e.ExceptionObject) ?? "Unknown unhandled exception"));

        try
        {
            if (args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
            {
                JsonAdapters.SelfTest();
                using var form = CreateMainForm();
                // Force handle creation too, so title-bar and control initialization
                // are covered without entering a permanent GUI message loop in CI.
                _ = form.Handle;
                return 0;
            }

            Application.Run(CreateMainForm());
            return 0;
        }
        catch (Exception ex)
        {
            var log = ReportCrash("Startup exception", ex);
            try
            {
                MessageBox.Show(
                    $"Cheeseburger DB Studio could not start.\n\n{ex.Message}\n\nCrash details were written to:\n{log}",
                    "DB Studio startup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // If even MessageBox cannot initialize, the log is still available.
            }
            return 1;
        }
    }

    private static MainForm CreateMainForm()
    {
        var form = new MainForm();
        try
        {
            var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (appIcon is not null)
                form.Icon = appIcon;
        }
        catch
        {
            // The executable resource still carries the release icon even if
            // Windows refuses to expose it through ExtractAssociatedIcon.
        }
        return form;
    }

    private static string ReportCrash(string heading, Exception ex)
    {
        string logPath;
        try
        {
            var baseDir = AppContext.BaseDirectory;
            logPath = Path.Combine(baseDir, "dbstudio-crash.log");
            var text = $"[{DateTimeOffset.Now:O}] {heading}\r\n{ex}\r\n\r\n";
            File.AppendAllText(logPath, text);
        }
        catch
        {
            logPath = Path.Combine(Path.GetTempPath(), "cheeseburger-dbstudio-crash.log");
            try
            {
                File.AppendAllText(logPath,
                    $"[{DateTimeOffset.Now:O}] {heading}\r\n{ex}\r\n\r\n");
            }
            catch
            {
                // Last resort: nothing more we can persist.
            }
        }
        return logPath;
    }
}
