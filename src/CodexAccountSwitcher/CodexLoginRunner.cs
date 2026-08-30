using System.Diagnostics;
using System.IO;

namespace CodexAccountSwitcher;

internal sealed class CodexLoginRunner
{
    internal async Task<int> RunAsync(string temporaryCodexHome, CancellationToken cancellationToken)
    {
        var executable = ResolveCodexExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = temporaryCodexHome,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        startInfo.Environment["CODEX_HOME"] = temporaryCodexHome;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Codex 登录程序");
        var standardOutput = DrainAsync(process.StandardOutput);
        var standardError = DrainAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        await Task.WhenAll(standardOutput, standardError);
        return process.ExitCode;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory()) > 0)
        {
            // OAuth URLs and command output are intentionally discarded rather than logged.
        }
    }

    private static string ResolveCodexExecutable()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), "codex.exe");
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                }
            }
        }

        var extensionsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode",
            "extensions");
        if (Directory.Exists(extensionsDirectory))
        {
            var candidate = Directory.EnumerateDirectories(
                    extensionsDirectory,
                    "openai.chatgpt-*-win32-x64",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(directory => Path.Combine(directory, "bin", "windows-x86_64", "codex.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("未找到 codex.exe，请先安装或更新 ChatGPT/Codex 扩展");
    }
}
