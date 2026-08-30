using System.IO;
using System.Windows;

namespace CodexAccountSwitcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var options = StartupOptions.Parse(e.Args);
            var window = new MainWindow(options);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Codex 账号切换",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

internal sealed record StartupOptions(string AuthDirectory, string? ScreenshotPath, bool DemoMode, bool ShowDemoAnalyzer)
{
    internal static StartupOptions Parse(string[] args)
    {
        var authDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string? screenshotPath = null;
        var demoMode = false;
        var showDemoAnalyzer = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--auth-dir" when index + 1 < args.Length:
                    authDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "--screenshot" when index + 1 < args.Length:
                    screenshotPath = Path.GetFullPath(args[++index]);
                    break;
                case "--demo":
                    demoMode = true;
                    break;
                case "--demo-analyzer":
                    showDemoAnalyzer = true;
                    break;
            }
        }

        return new StartupOptions(Path.GetFullPath(authDirectory), screenshotPath, demoMode, showDemoAnalyzer);
    }
}
