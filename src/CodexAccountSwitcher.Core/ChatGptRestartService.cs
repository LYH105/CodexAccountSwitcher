using System.Diagnostics;
using System.ComponentModel;

namespace CodexAccountSwitcher.Core;

public sealed class ChatGptRestartService
{
    public const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private readonly IChatGptRuntime _runtime;
    private readonly TimeSpan _gracefulTimeout;
    private readonly TimeSpan _exitTimeout;
    private readonly TimeSpan _activationTimeout;
    private readonly TimeSpan _pollInterval;

    public ChatGptRestartService()
        : this(
            new WindowsChatGptRuntime(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(12),
            TimeSpan.FromMilliseconds(180))
    {
    }

    internal ChatGptRestartService(
        IChatGptRuntime runtime,
        TimeSpan gracefulTimeout,
        TimeSpan exitTimeout,
        TimeSpan activationTimeout,
        TimeSpan pollInterval)
    {
        _runtime = runtime;
        _gracefulTimeout = gracefulTimeout;
        _exitTimeout = exitTimeout;
        _activationTimeout = activationTimeout;
        _pollInterval = pollInterval;
    }

    public async Task<ChatGptRestartResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        var stop = await StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stop.Success)
        {
            return new ChatGptRestartResult(
                false,
                stop.ClosedProcessCount,
                stop.ForcedProcessCount,
                stop.ErrorMessage);
        }

        var start = await StartAsync(cancellationToken).ConfigureAwait(false);
        return new ChatGptRestartResult(
            start.Success,
            stop.ClosedProcessCount,
            stop.ForcedProcessCount,
            start.ErrorMessage);
    }

    public async Task<ChatGptStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var initialProcesses = _runtime.ListOwnedProcesses();
            foreach (var process in initialProcesses)
            {
                _runtime.RequestClose(process.Id);
            }

            await WaitForAsync(
                () => _runtime.ListOwnedProcesses().Count == 0,
                _gracefulTimeout,
                cancellationToken).ConfigureAwait(false);

            var remainingProcesses = _runtime.ListOwnedProcesses();
            var forcedCount = 0;
            foreach (var process in remainingProcesses)
            {
                if (_runtime.ForceTerminate(process.Id))
                {
                    forcedCount++;
                }
            }

            var exited = await WaitForAsync(
                () => _runtime.ListOwnedProcesses().Count == 0,
                _exitTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!exited)
            {
                return new ChatGptStopResult(
                    false,
                    initialProcesses.Count,
                    forcedCount,
                    "Store 版 ChatGPT 仍有进程未退出");
            }

            return new ChatGptStopResult(true, initialProcesses.Count, forcedCount, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new ChatGptStopResult(false, 0, 0, exception.Message);
        }
    }

    public async Task<ChatGptStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_runtime.ListOwnedProcesses().Any(process =>
                    process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)))
            {
                return new ChatGptStartResult(true, null);
            }

            _runtime.Activate(AppUserModelId);
            var activated = await WaitForAsync(
                () => _runtime.ListOwnedProcesses().Any(process =>
                    process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)),
                _activationTimeout,
                cancellationToken).ConfigureAwait(false);
            return activated
                ? new ChatGptStartResult(true, null)
                : new ChatGptStartResult(false, "系统没有启动新的 ChatGPT 进程");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new ChatGptStartResult(false, exception.Message);
        }
    }

    internal static bool IsOwnedExecutable(string processName, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !executablePath.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = executablePath.Replace('/', '\\');
        return (processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
               || (processName.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
                   normalized.EndsWith("\\app\\resources\\codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> WaitForAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (predicate())
        {
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            if (predicate())
            {
                return true;
            }
        }

        return predicate();
    }
}

internal sealed record OwnedProcess(int Id, string ProcessName, string ExecutablePath);

internal interface IChatGptRuntime
{
    IReadOnlyList<OwnedProcess> ListOwnedProcesses();

    bool RequestClose(int processId);

    bool ForceTerminate(int processId);

    void Activate(string appUserModelId);
}

internal sealed class WindowsChatGptRuntime : IChatGptRuntime
{
    public IReadOnlyList<OwnedProcess> ListOwnedProcesses()
    {
        var results = new List<OwnedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? executablePath;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
                {
                    continue;
                }

                if (ChatGptRestartService.IsOwnedExecutable(process.ProcessName, executablePath))
                {
                    results.Add(new OwnedProcess(process.Id, process.ProcessName, executablePath!));
                }
            }
        }

        return results;
    }

    public bool RequestClose(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.CloseMainWindow();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public bool ForceTerminate(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Activate(string appUserModelId)
    {
        if (!appUserModelId.Equals(ChatGptRestartService.AppUserModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝启动未知的 Store 应用标识");
        }

        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        if (!File.Exists(explorerPath))
        {
            throw new IOException("未找到 Windows Store 应用启动器 explorer.exe");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = explorerPath,
            Arguments = $"shell:AppsFolder\\{appUserModelId}",
            UseShellExecute = true,
        });
    }
}
