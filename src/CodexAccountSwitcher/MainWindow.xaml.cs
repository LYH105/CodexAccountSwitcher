using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly StartupOptions _options;
    private readonly CancellationTokenSource _closingSource = new();
    private AccountCardViewModel? _pendingSwitch;
    private AccountCardViewModel? _pendingDelete;
    private AccountCardViewModel? _analyticsAccount;
    private CancellationTokenSource? _loginSource;
    private CancellationTokenSource? _analyticsSource;

    internal MainWindow(StartupOptions options)
    {
        _options = options;
        _viewModel = new MainViewModel(options.AuthDirectory, options.DemoMode);
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync(_closingSource.Token);
        if (_options.DemoMode && _options.ShowDemoAnalyzer && _viewModel.Accounts.FirstOrDefault() is { } account)
        {
            await OpenAnalyticsAsync(account);
        }
        if (_options.ScreenshotPath is not null)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            SaveScreenshot(_options.ScreenshotPath);
            Close();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync(_closingSource.Token);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanLogin)
        {
            return;
        }

        _loginSource = CancellationTokenSource.CreateLinkedTokenSource(_closingSource.Token);
        CancelLoginButton.IsEnabled = true;
        LoginProgressText.Text = "官方 Codex 登录页将自动打开，完成登录后此窗口会继续处理。";
        LoginOverlay.Visibility = Visibility.Visible;
        try
        {
            await _viewModel.LoginNewAccountAsync(_loginSource.Token);
        }
        finally
        {
            LoginOverlay.Visibility = Visibility.Collapsed;
            _loginSource.Dispose();
            _loginSource = null;
        }
    }

    private void CancelLoginButton_Click(object sender, RoutedEventArgs e)
    {
        CancelLoginButton.IsEnabled = false;
        LoginProgressText.Text = "正在取消并清理临时登录…";
        _loginSource?.Cancel();
    }

    private void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountCardViewModel account } || !account.CanSwitch)
        {
            return;
        }

        _pendingSwitch = account;
        _pendingDelete = null;
        ConfirmTitleText.Text = "确认切换账号";
        ConfirmAccountText.Text = $"{account.DisplayName}  ·  {account.Summary.Email}";
        ConfirmAccountText.Foreground = (Brush)FindResource("AccentBrush");
        ConfirmBodyText.Text = "工具会交换目标账号与当前账号，旧的当前凭据会完整保存；随后完整关闭并重新启动 Store 版 ChatGPT。ChatGPT 内正在运行的 Codex 任务会被中断，VS Code 窗口及其扩展进程不会被关闭。";
        ConfirmIconBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0xD6));
        ConfirmIconPath.Stroke = (Brush)FindResource("WarningBrush");
        ConfirmActionButton.Style = (Style)FindResource("PrimaryButtonStyle");
        ConfirmActionButton.Content = "切换并重启";
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountCardViewModel account } || !account.CanDelete)
        {
            return;
        }

        _pendingSwitch = null;
        _pendingDelete = account;
        ConfirmTitleText.Text = "确认删除账号";
        ConfirmAccountText.Text = $"{account.DisplayName}  ·  {account.Summary.Email}";
        ConfirmAccountText.Foreground = (Brush)FindResource("DangerBrush");
        ConfirmBodyText.Text = "此操作会永久删除该账号保存的本地登录凭据，删除后无法在工具内恢复。当前账号不会退出，ChatGPT 也不会重启。";
        ConfirmIconBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xF1));
        ConfirmIconPath.Stroke = (Brush)FindResource("DangerBrush");
        ConfirmActionButton.Style = (Style)FindResource("DangerButtonStyle");
        ConfirmActionButton.Content = "永久删除";
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountCardViewModel account } || !account.CanAnalyze)
        {
            return;
        }

        await OpenAnalyticsAsync(account);
    }

    private async Task OpenAnalyticsAsync(AccountCardViewModel account)
    {
        _analyticsAccount = account;
        AnalyticsAccountText.Text = $"{account.DisplayName}  ·  {account.Summary.Email}";
        SetAnalyticsRange(30);
        AnalyticsOverlay.Visibility = Visibility.Visible;
        await RunAnalyticsAsync();
    }

    private async void RunAnalyticsButton_Click(object sender, RoutedEventArgs e) => await RunAnalyticsAsync();

    private void AnalyticsSevenDaysButton_Click(object sender, RoutedEventArgs e) => SetAnalyticsRange(7);

    private void AnalyticsThirtyDaysButton_Click(object sender, RoutedEventArgs e) => SetAnalyticsRange(30);

    private void CloseAnalyticsButton_Click(object sender, RoutedEventArgs e) => CloseAnalytics();

    private async Task RunAnalyticsAsync()
    {
        var account = _analyticsAccount;
        if (account is null)
        {
            return;
        }

        if (AnalyticsStartDatePicker.SelectedDate is not DateTime start ||
            AnalyticsEndDatePicker.SelectedDate is not DateTime end)
        {
            ShowAnalyticsError("请选择开始日期和结束日期");
            return;
        }

        var startDate = DateOnly.FromDateTime(start);
        var endDate = DateOnly.FromDateTime(end);
        if (endDate < startDate)
        {
            ShowAnalyticsError("开始日期不能晚于结束日期");
            return;
        }

        _analyticsSource?.Cancel();
        _analyticsSource?.Dispose();
        _analyticsSource = CancellationTokenSource.CreateLinkedTokenSource(_closingSource.Token);
        RunAnalyticsButton.IsEnabled = false;
        AnalyticsLoadingPanel.Visibility = Visibility.Visible;
        AnalyticsErrorPanel.Visibility = Visibility.Collapsed;
        AnalyticsResultPanel.Visibility = Visibility.Collapsed;
        AnalyticsSourceText.Text = "正在查询，令牌仅在后端内存中使用";

        try
        {
            var result = await _viewModel.AnalyzeAsync(account, startDate, endDate, _analyticsSource.Token);
            if (result.Status != TokenCostStatus.Available)
            {
                ShowAnalyticsError(result.ErrorMessage ?? "精确分析失败");
                return;
            }

            ShowAnalyticsResult(result);
        }
        catch (OperationCanceledException)
        {
            if (AnalyticsOverlay.Visibility == Visibility.Visible)
            {
                ShowAnalyticsError("精确分析已取消");
            }
        }
        finally
        {
            RunAnalyticsButton.IsEnabled = true;
            AnalyticsLoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowAnalyticsResult(TokenCostAnalysis result)
    {
        AnalyticsTotalTokensText.Text = FormatCompact(result.TotalTokens);
        AnalyticsTotalTokensText.ToolTip = $"{result.TotalTokens:N0}";
        AnalyticsCacheRateText.Text = $"{result.CacheHitRate:P1}";
        AnalyticsCreditsText.Text = $"{result.ComputedCredits:N2}";
        AnalyticsApiCostText.Text = $"${result.ApiStandardUsd:N4}";
        AnalyticsUncachedText.Text = FormatCompact(result.UncachedInputTokens);
        AnalyticsCachedText.Text = FormatCompact(result.CachedInputTokens);
        AnalyticsOutputText.Text = FormatCompact(result.OutputTokens);
        AnalyticsTurnsText.Text = $"{result.Turns:N0} / {result.ActiveDays} 天";
        AnalyticsCoverageText.Text = $"{result.ExactModelTokenCoverage:P1}";
        AnalyticsCoverageText.ToolTip = $"已定价精确 Token 覆盖率 {result.PricedTokenCoverage:P1}";
        AnalyticsModelsList.ItemsSource = result.Models.Select(model => new AnalyticsModelDisplay(model)).ToArray();
        AnalyticsWarningText.Text = result.Warning ?? string.Empty;
        AnalyticsWarningPanel.Visibility = string.IsNullOrWhiteSpace(result.Warning)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AnalyticsSourceText.Text =
            $"{result.DataSource} · v{result.AnalyzerVersion} · 发生日 ${result.ApiAtUseUsd:N4} · 同速度 ${result.ApiMatchedSpeedUsd:N4} · 上报 Credits {result.ReportedCredits:N2}";
        AnalyticsErrorPanel.Visibility = Visibility.Collapsed;
        AnalyticsResultPanel.Visibility = Visibility.Visible;
    }

    private void ShowAnalyticsError(string message)
    {
        AnalyticsErrorText.Text = message;
        AnalyticsErrorPanel.Visibility = Visibility.Visible;
        AnalyticsResultPanel.Visibility = Visibility.Collapsed;
        AnalyticsLoadingPanel.Visibility = Visibility.Collapsed;
        AnalyticsSourceText.Text = "未保存接口原始响应或认证令牌";
    }

    private void SetAnalyticsRange(int days)
    {
        var today = DateTime.Today;
        AnalyticsStartDatePicker.SelectedDate = today.AddDays(-(days - 1));
        AnalyticsEndDatePicker.SelectedDate = today;
    }

    private void CloseAnalytics()
    {
        _analyticsSource?.Cancel();
        _analyticsSource?.Dispose();
        _analyticsSource = null;
        _analyticsAccount = null;
        AnalyticsOverlay.Visibility = Visibility.Collapsed;
    }

    private void CancelSwitchButton_Click(object sender, RoutedEventArgs e) => CloseConfirmation();

    private async void ConfirmActionButton_Click(object sender, RoutedEventArgs e)
    {
        var switchAccount = _pendingSwitch;
        var deleteAccount = _pendingDelete;
        CloseConfirmation();
        if (switchAccount is not null)
        {
            await _viewModel.SwitchAsync(switchAccount, _closingSource.Token);
        }
        else if (deleteAccount is not null)
        {
            await _viewModel.DeleteAccountAsync(deleteAccount, _closingSource.Token);
        }
    }

    private void CloseConfirmation()
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _pendingSwitch = null;
        _pendingDelete = null;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && AnalyticsOverlay.Visibility == Visibility.Visible)
        {
            CloseAnalytics();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ConfirmOverlay.Visibility == Visibility.Visible)
        {
            CloseConfirmation();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && LoginOverlay.Visibility == Visibility.Visible)
        {
            CancelLoginButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closingSource.Cancel();
        _loginSource?.Cancel();
        _analyticsSource?.Cancel();
        _analyticsSource?.Dispose();
        _closingSource.Dispose();
        _viewModel.Dispose();
    }

    private static string FormatCompact(decimal value)
    {
        var absolute = Math.Abs(value);
        return absolute switch
        {
            >= 1_000_000_000m => $"{value / 1_000_000_000m:0.##}B",
            >= 1_000_000m => $"{value / 1_000_000m:0.##}M",
            >= 1_000m => $"{value / 1_000m:0.##}K",
            _ => $"{value:0}",
        };
    }

    private sealed class AnalyticsModelDisplay(TokenCostModelRow row)
    {
        public string Label { get; } = row.Label;

        public string AccuracyText { get; } = row.IsExact
            ? row.IsPriced ? "模型级精确 Token" : "精确 Token · 费率未收录"
            : "回退估算";

        public string SpeedText { get; } = row.Speed == "fast" ? "Fast" : "Standard";

        public string TokenText { get; } = FormatCompact(row.TotalTokens);

        public string CreditsText { get; } = $"{row.CodexCredits:N2}";

        public string ApiCostText { get; } = row.IsPriced ? $"${row.ApiStandardUsd:N4}" : "未定价";
    }

    private void SaveScreenshot(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bounds = VisualTreeHelper.GetDescendantBounds(this);
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(this), null, new Rect(new Point(), bounds.Size));
        }

        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
