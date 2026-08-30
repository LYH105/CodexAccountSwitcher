using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class MainViewModel : NotifyObject, IDisposable
{
    private readonly AuthAccountCatalog _catalog;
    private readonly AuthSwitchService _switchService;
    private readonly AccountLoginService _loginService;
    private readonly CodexLoginRunner _loginRunner;
    private readonly QuotaClient _quotaClient;
    private readonly WorkspaceNameClient _workspaceNameClient;
    private readonly TokenCostAnalyzerClient _tokenCostAnalyzer;
    private readonly ChatGptRestartService _chatGptRestartService;
    private readonly bool _demoMode;
    private bool _isRefreshing;
    private bool _isLoggingIn;
    private string _currentAccountText = "正在读取账号…";
    private string _statusMessage = "额度仅在打开工具或点击刷新时查询";
    private string _lastRefreshText = "尚未刷新";

    public MainViewModel(string authDirectory, bool demoMode)
    {
        AuthDirectory = authDirectory;
        _catalog = new AuthAccountCatalog(authDirectory);
        _switchService = new AuthSwitchService(authDirectory);
        _loginService = new AccountLoginService(authDirectory);
        _loginRunner = new CodexLoginRunner();
        _quotaClient = new QuotaClient(authDirectory);
        _workspaceNameClient = new WorkspaceNameClient(authDirectory);
        _tokenCostAnalyzer = new TokenCostAnalyzerClient(authDirectory);
        _chatGptRestartService = new ChatGptRestartService();
        _demoMode = demoMode;
    }

    public ObservableCollection<AccountCardViewModel> Accounts { get; } = [];

    public string AuthDirectory { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetField(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanLogin));
            }
        }
    }

    public bool CanRefresh => !IsRefreshing && !IsLoggingIn;

    public bool IsLoggingIn
    {
        get => _isLoggingIn;
        private set
        {
            if (SetField(ref _isLoggingIn, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanLogin));
                OnPropertyChanged(nameof(LoginButtonText));
                foreach (var account in Accounts)
                {
                    account.IsGlobalBusy = value;
                }
            }
        }
    }

    public bool CanLogin => !IsRefreshing && !IsLoggingIn;

    public string LoginButtonText => IsLoggingIn ? "等待登录…" : "登录新账号";

    public string CurrentAccountText
    {
        get => _currentAccountText;
        private set => SetField(ref _currentAccountText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set => SetField(ref _lastRefreshText, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loginRecovered = await Task.Run(_loginService.RecoverPending, cancellationToken);
            var switchRecovered = await Task.Run(_switchService.RecoverPending, cancellationToken);
            LoadAccounts();
            if (loginRecovered || switchRecovered)
            {
                StatusMessage = "已自动恢复上次中断的账号操作";
            }

            if (_demoMode)
            {
                ApplyDemoQuotas();
                LastRefreshText = "刚刚刷新";
            }
            else
            {
                await RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        StatusMessage = "正在并发查询全部账号额度与 Team 名称…";
        foreach (var account in Accounts)
        {
            account.SetLoading();
        }

        try
        {
            var tasks = Accounts.Select(async account =>
            {
                if (!account.Summary.IsUsable)
                {
                    return (
                        account,
                        quota: QuotaInfo.Failure(QuotaStatus.Error, account.Summary.ErrorMessage ?? "账号不可用"),
                        workspace: WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, account.Summary.ErrorMessage ?? "账号不可用"));
                }

                var quotaTask = _quotaClient.QueryAsync(account.Summary.Slot, cancellationToken);
                var workspaceTask = WorkspaceNameClient.RequiresLookup(account.Summary.Plan)
                    ? _workspaceNameClient.QueryAsync(account.Summary.Slot, cancellationToken)
                    : Task.FromResult(WorkspaceNameInfo.NotRequired());
                await Task.WhenAll(quotaTask, workspaceTask);
                return (account, quota: await quotaTask, workspace: await workspaceTask);
            });

            var results = await Task.WhenAll(tasks);
            foreach (var (account, quota, workspace) in results)
            {
                account.ApplyWorkspaceName(workspace);
                account.ApplyQuota(quota);
            }

            var succeeded = results.Count(result => result.quota.Status == QuotaStatus.Available);
            var workspaceResults = results
                .Where(result => WorkspaceNameClient.RequiresLookup(result.account.Summary.Plan))
                .ToArray();
            var workspaceSucceeded = workspaceResults.Count(result => result.workspace.Status == WorkspaceNameStatus.Available);
            StatusMessage = workspaceResults.Length == 0
                ? $"已完成额度查询：{succeeded}/{results.Length} 个账号成功"
                : $"已完成：额度 {succeeded}/{results.Length}，Team 名称 {workspaceSucceeded}/{workspaceResults.Length}";
            LastRefreshText = $"{DateTime.Now:HH:mm:ss} 刷新";
            UpdateCurrentAccountText();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "额度查询已取消";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task SwitchAsync(AccountCardViewModel account, CancellationToken cancellationToken)
    {
        if (!account.CanSwitch)
        {
            return;
        }

        var switched = false;
        var chatGptStopped = false;
        account.IsSwitching = true;
        foreach (var item in Accounts)
        {
            item.IsGlobalBusy = true;
        }
        StatusMessage = "正在关闭 Store 版 ChatGPT，确认旧会话完全退出…";
        try
        {
            var stop = await _chatGptRestartService.StopAsync(cancellationToken);
            if (!stop.Success)
            {
                StatusMessage = $"未切换账号：无法完整关闭 ChatGPT：{stop.ErrorMessage}";
                return;
            }

            chatGptStopped = true;
            StatusMessage = $"ChatGPT 已退出，正在切换到 {account.DisplayName}…";
            await Task.Run(
                () => _switchService.Switch(account.Summary.Slot, account.Summary.ContentHash),
                cancellationToken);
            switched = true;
            LoadAccounts();
            foreach (var item in Accounts)
            {
                item.IsGlobalBusy = true;
            }
            StatusMessage = "账号已切换，正在启动并验证新的 ChatGPT 进程…";
            var start = await _chatGptRestartService.StartAsync(cancellationToken);
            var finalStatus = start.Success
                ? $"账号切换成功，ChatGPT 已使用新账号重启（关闭 {stop.ClosedProcessCount} 个旧进程）"
                : $"账号已切换，但 ChatGPT 启动失败：{start.ErrorMessage}";
            await RefreshAsync(cancellationToken);
            StatusMessage = finalStatus;
        }
        catch (OperationCanceledException)
        {
            var recovery = chatGptStopped
                ? await _chatGptRestartService.StartAsync(CancellationToken.None)
                : null;
            StatusMessage = switched
                ? recovery?.Success == true
                    ? "账号已经切换，ChatGPT 已启动；额度刷新被取消"
                    : $"账号已经切换，但恢复启动 ChatGPT 失败：{recovery?.ErrorMessage}"
                : recovery?.Success == true
                    ? "账号切换已取消，原 ChatGPT 已重新启动"
                    : "账号切换已取消";
        }
        catch (Exception exception)
        {
            var recovery = chatGptStopped
                ? await _chatGptRestartService.StartAsync(CancellationToken.None)
                : null;
            StatusMessage = switched
                ? recovery?.Success == true
                    ? $"账号已切换并恢复启动 ChatGPT，但后续操作失败：{exception.Message}"
                    : $"账号已经切换，但 ChatGPT 启动失败：{recovery?.ErrorMessage ?? exception.Message}"
                : recovery?.Success == true
                    ? $"切换失败，原 ChatGPT 已恢复启动：{exception.Message}"
                    : $"切换失败：{exception.Message}";
        }
        finally
        {
            account.IsSwitching = false;
            foreach (var item in Accounts)
            {
                item.IsGlobalBusy = false;
            }
        }
    }

    public async Task DeleteAccountAsync(AccountCardViewModel account, CancellationToken cancellationToken)
    {
        if (!account.CanDelete)
        {
            return;
        }

        var displayName = account.DisplayName;
        account.IsDeleting = true;
        foreach (var item in Accounts)
        {
            item.IsGlobalBusy = true;
        }

        StatusMessage = $"正在删除 {displayName}…";
        try
        {
            await Task.Run(
                () => _switchService.DeleteSavedAccount(account.Summary.Slot, account.Summary.ContentHash),
                cancellationToken);
            LoadAccounts();
            StatusMessage = $"已删除账号：{displayName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "删除账号已取消";
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除失败：{exception.Message}";
        }
        finally
        {
            account.IsDeleting = false;
            foreach (var item in Accounts)
            {
                item.IsGlobalBusy = false;
            }
        }
    }

    public async Task LoginNewAccountAsync(CancellationToken cancellationToken)
    {
        if (!CanLogin)
        {
            return;
        }

        LoginResult? result = null;
        IsLoggingIn = true;
        StatusMessage = "已打开官方登录流程，请在浏览器中完成登录…";
        try
        {
            result = await _loginService.LoginNewAccountAsync(_loginRunner.RunAsync, cancellationToken);
            LoadAccounts();
            foreach (var account in Accounts)
            {
                account.IsGlobalBusy = true;
            }

            StatusMessage = result.RefreshedCurrentAccount
                ? $"已刷新当前账号：{result.DisplayName}"
                : result.ReusedExistingSlot
                    ? $"已登录 {result.DisplayName}，原账号已安全回存"
                    : $"已登录 {result.DisplayName}，原账号已安全保存";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "新账号登录已取消，当前账号保持不变";
        }
        catch (Exception exception)
        {
            StatusMessage = $"登录失败：{exception.Message}";
        }
        finally
        {
            IsLoggingIn = false;
        }

        if (result is not null)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    public Task<TokenCostAnalysis> AnalyzeAsync(
        AccountCardViewModel account,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        return _demoMode
            ? Task.FromResult(CreateDemoAnalysis(startDate, endDate))
            : _tokenCostAnalyzer.QueryAsync(account.Summary.Slot, startDate, endDate, cancellationToken);
    }

    public void Dispose()
    {
        _quotaClient.Dispose();
        _workspaceNameClient.Dispose();
        _tokenCostAnalyzer.Dispose();
    }

    private void LoadAccounts()
    {
        var snapshots = _catalog.Load();
        if (_demoMode && snapshots.Count == 0)
        {
            snapshots =
            [
                new AccountSummary("auth.json", true, "星河研发团队", "lin@example.test", "Team", "demo-a", new string('A', 64), DateTimeOffset.Now.AddHours(1), true, null),
                new AccountSummary("auth.json0", false, "陈予安", "chen@example.test", "Plus", "demo-b", new string('B', 64), DateTimeOffset.Now.AddHours(1), true, null),
                new AccountSummary("auth.json1", false, "远山工作室", "zhou@example.test", "Team", "demo-c", new string('C', 64), DateTimeOffset.Now.AddHours(1), true, null),
                new AccountSummary("auth.json2", false, "苏念", "su@example.test", "Pro", "demo-d", new string('D', 64), DateTimeOffset.Now.AddHours(1), true, null),
            ];
        }
        Accounts.Clear();
        foreach (var snapshot in snapshots)
        {
            Accounts.Add(new AccountCardViewModel(snapshot) { IsGlobalBusy = IsLoggingIn });
        }

        if (_demoMode)
        {
            foreach (var account in Accounts.Where(account => WorkspaceNameClient.RequiresLookup(account.Summary.Plan)))
            {
                account.ApplyWorkspaceName(new WorkspaceNameInfo(WorkspaceNameStatus.Available, account.Summary.DisplayName, null));
            }
        }

        UpdateCurrentAccountText();
    }

    private void UpdateCurrentAccountText()
    {
        var current = Accounts.FirstOrDefault(account => account.Summary.IsCurrent);
        CurrentAccountText = current is null
            ? "未找到当前账号"
            : $"{current.DisplayName}  ·  {current.PlanText}";
    }

    private void ApplyDemoQuotas()
    {
        for (var index = 0; index < Accounts.Count; index++)
        {
            var now = DateTimeOffset.Now;
            var primary = new UsageWindow(18 + index * 11, 18_000, now.AddHours(3 + index));
            Accounts[index].ApplyQuota(new QuotaInfo(
                QuotaStatus.Available,
                Accounts[index].Summary.Plan,
                primary,
                null,
                index == 0 ? 42.50m : null,
                now,
                null));
        }

        StatusMessage = "额度仅在打开工具或点击刷新时查询";
    }

    private static TokenCostAnalysis CreateDemoAnalysis(DateOnly startDate, DateOnly endDate)
    {
        var models = new[]
        {
            new TokenCostModelRow(
                "gpt-5.6-sol",
                "GPT-5.6 Sol",
                "standard",
                2_480_000,
                8_760_000,
                1_120_000,
                12_360_000,
                1_258,
                50.48m,
                50.48m,
                50.48m,
                true,
                true),
            new TokenCostModelRow(
                "gpt-5.6-terra",
                "GPT-5.6 Terra",
                "fast",
                860_000,
                2_140_000,
                480_000,
                3_480_000,
                727.19m,
                7.908m,
                7.908m,
                15.816m,
                true,
                true),
        };
        var uncached = models.Sum(model => model.UncachedInputTokens);
        var cached = models.Sum(model => model.CachedInputTokens);
        var output = models.Sum(model => model.OutputTokens);
        return new TokenCostAnalysis(
            TokenCostStatus.Available,
            startDate,
            endDate,
            uncached,
            cached,
            output,
            models.Sum(model => model.TotalTokens),
            models.Sum(model => model.CodexCredits),
            1_982.4m,
            models.Sum(model => model.ApiStandardUsd),
            models.Sum(model => model.ApiAtUseUsd),
            models.Sum(model => model.ApiMatchedSpeedUsd),
            models.Where(model => model.Speed == "fast").Sum(model => model.CodexCredits),
            cached / (uncached + cached),
            1m,
            1m,
            386,
            18,
            "Workspace Token + Breakdown 交叉校验",
            TokenCostAnalyzerClient.AnalyzerVersion,
            DateTimeOffset.Now,
            models,
            null,
            null);
    }
}

public sealed class AccountCardViewModel : NotifyObject
{
    private QuotaInfo? _quota;
    private WorkspaceNameInfo? _workspaceName;
    private bool _isLoading;
    private bool _isSwitching;
    private bool _isDeleting;
    private bool _isGlobalBusy;

    public AccountCardViewModel(AccountSummary summary)
    {
        Summary = summary;
    }

    public AccountSummary Summary { get; }

    public string DisplayName
    {
        get
        {
            if (!WorkspaceNameClient.RequiresLookup(Summary.Plan))
            {
                return Summary.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(_workspaceName?.Name))
            {
                return _workspaceName.Name;
            }

            if (!string.IsNullOrWhiteSpace(Summary.WorkspaceName))
            {
                return Summary.WorkspaceName;
            }

            return _workspaceName is null
                ? "正在读取 Team 名称…"
                : "Team 名称读取失败";
        }
    }

    public string DisplayNameToolTip => !string.IsNullOrWhiteSpace(Summary.WorkspaceName) ||
                                        _workspaceName?.Status == WorkspaceNameStatus.Available
        ? DisplayName
        : _workspaceName?.ErrorMessage ?? DisplayName;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                NotifyComputed();
            }
        }
    }

    public bool IsSwitching
    {
        get => _isSwitching;
        set
        {
            if (SetField(ref _isSwitching, value))
            {
                NotifyComputed();
            }
        }
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        set
        {
            if (SetField(ref _isDeleting, value))
            {
                NotifyComputed();
            }
        }
    }

    public bool IsGlobalBusy
    {
        get => _isGlobalBusy;
        set
        {
            if (SetField(ref _isGlobalBusy, value))
            {
                NotifyComputed();
            }
        }
    }

    public bool IsQuotaVisible => _quota?.Status == QuotaStatus.Available;

    public bool HasQuotaError => !IsLoading && _quota is { Status: not QuotaStatus.Available };

    public bool CanSwitch => Summary.IsUsable && !Summary.IsCurrent && !IsSwitching && !IsGlobalBusy;

    public bool CanAnalyze => Summary.IsUsable && !IsGlobalBusy;

    public bool IsDeleteVisible => !Summary.IsCurrent;

    public bool CanDelete => IsDeleteVisible &&
                             !IsDeleting &&
                             !IsGlobalBusy &&
                             Summary.ContentHash.Length == 64;

    public string SwitchButtonText => Summary.IsCurrent
        ? "当前账号"
        : IsSwitching ? "切换中…" : "切换到此账号";

    public string DeleteButtonText => IsDeleting ? "删除中…" : "删除账号";

    public string QuotaStateText => IsLoading
        ? "正在查询额度…"
        : _quota?.ErrorMessage ?? Summary.ErrorMessage ?? "等待查询";

    public string PlanText => NormalizePlan(_quota?.Plan) ?? Summary.Plan;

    public string PrimaryTitle => WindowTitle(_quota?.Primary, "主要额度");

    public double PrimaryRemainingPercent => _quota?.Primary?.RemainingPercent ?? 0d;

    public string PrimaryRemainingText => RemainingText(_quota?.Primary);

    public string PrimaryResetText => ResetText(_quota?.Primary);

    public bool HasCredits => _quota?.CreditBalance is not null;

    public string CreditsText => _quota?.CreditBalance is decimal credits
        ? $"Credits  {credits:0.##}"
        : string.Empty;

    public void SetLoading()
    {
        IsLoading = true;
        _quota = null;
        NotifyComputed();
    }

    public void ApplyQuota(QuotaInfo quota)
    {
        _quota = quota;
        IsLoading = false;
        NotifyComputed();
    }

    public void ApplyWorkspaceName(WorkspaceNameInfo workspaceName)
    {
        if (workspaceName.Status == WorkspaceNameStatus.Available || _workspaceName?.Status != WorkspaceNameStatus.Available)
        {
            _workspaceName = workspaceName;
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayNameToolTip));
    }

    private void NotifyComputed()
    {
        OnPropertyChanged(nameof(IsQuotaVisible));
        OnPropertyChanged(nameof(HasQuotaError));
        OnPropertyChanged(nameof(CanSwitch));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(IsDeleteVisible));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(SwitchButtonText));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(QuotaStateText));
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(PrimaryTitle));
        OnPropertyChanged(nameof(PrimaryRemainingPercent));
        OnPropertyChanged(nameof(PrimaryRemainingText));
        OnPropertyChanged(nameof(PrimaryResetText));
        OnPropertyChanged(nameof(HasCredits));
        OnPropertyChanged(nameof(CreditsText));
    }

    private static string RemainingText(UsageWindow? window) =>
        window is null ? "—" : $"{window.RemainingPercent:0.#}% 可用";

    private static string ResetText(UsageWindow? window)
    {
        if (window?.ResetAt is null)
        {
            return "无重置时间";
        }

        var local = window.ResetAt.Value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? $"今天 {local:HH:mm} 重置"
            : $"{local:M月d日 HH:mm} 重置";
    }

    private static string WindowTitle(UsageWindow? window, string fallback)
    {
        return window?.LimitWindowSeconds switch
        {
            <= 21_600 and > 0 => "5 小时额度",
            >= 518_400 => "每周额度",
            > 0 and var seconds => $"{Math.Round(seconds / 3600d):0} 小时额度",
            _ => fallback,
        };
    }

    private static string? NormalizePlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return null;
        }

        return plan.Trim().ToLowerInvariant() switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "free" => "Free",
            _ => plan.Trim(),
        };
    }
}

public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
