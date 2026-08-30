using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("账号解析与脱敏 DTO", TestCatalogParsingAsync),
        ("损坏 JSON 与重复账号", TestInvalidAndDuplicateAsync),
        ("路径穿越与异常槽位", TestPathValidationAsync),
        ("直接删除失效或损坏的保存账号", TestDeleteSavedAccountAsync),
        ("额度 200", TestQuotaSuccessAsync),
        ("额度 401/403", TestQuotaAuthFailuresAsync),
        ("额度超时与无效响应", TestQuotaTimeoutAndInvalidAsync),
        ("Team 名称按账号 ID 精确匹配", TestWorkspaceNameSuccessAsync),
        ("Team 名称失败隔离与个人套餐跳过", TestWorkspaceNameFailuresAsync),
        ("精确 Token 与 Fast 成本计算", TestTokenCostExactAsync),
        ("精确分析回退标记与 401", TestTokenCostFallbackAndAuthAsync),
        ("ChatGPT 重启范围与启动验证", TestChatGptRestartAsync),
        ("事务交换与哈希前置校验", TestSwitchAndHashGuardAsync),
        ("全部崩溃阶段恢复", TestCrashRecoveryAsync),
        ("登录全新账号并归档当前账号", TestLoginBrandNewAccountAsync),
        ("登录已有槽位账号并交换", TestLoginExistingAccountAsync),
        ("重复登录当前账号与失败回滚", TestLoginCurrentAndFailureAsync),
        ("登录导入全部崩溃阶段恢复", TestLoginCrashRecoveryAsync),
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--inspect", var authDirectory])
        {
            return await InspectLiveAsync(authDirectory);
        }
        if (args is ["--analytics-inspect", var analyticsDirectory])
        {
            return await InspectAnalyticsLiveAsync(analyticsDirectory);
        }

        var failures = new List<string>();
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.Message}");
                Console.WriteLine($"FAIL  {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"RESULT {Tests.Count - failures.Count}/{Tests.Count} passed");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task<int> InspectLiveAsync(string authDirectory)
    {
        var accounts = new AuthAccountCatalog(authDirectory).Load();
        using var quotaClient = new QuotaClient(authDirectory);
        using var workspaceNameClient = new WorkspaceNameClient(authDirectory);
        var results = await Task.WhenAll(accounts.Select(async account => new
        {
            account.Slot,
            account.IsCurrent,
            account.Plan,
            account.IsUsable,
            LocalWorkspaceName = account.WorkspaceName,
            Quota = account.IsUsable
                ? await quotaClient.QueryAsync(account.Slot)
                : QuotaInfo.Failure(QuotaStatus.Error, account.ErrorMessage ?? "invalid"),
            Workspace = account.IsUsable && WorkspaceNameClient.RequiresLookup(account.Plan)
                ? await workspaceNameClient.QueryAsync(account.Slot)
                : WorkspaceNameInfo.NotRequired(),
        }));
        var safe = results.Select(result => new
        {
            result.Slot,
            result.IsCurrent,
            result.Plan,
            result.IsUsable,
            DisplayName = result.Workspace.Name ?? result.LocalWorkspaceName,
            WorkspaceStatus = result.Workspace.Status.ToString(),
            QuotaStatus = result.Quota.Status.ToString(),
            PrimaryRemaining = result.Quota.Primary?.RemainingPercent,
            SecondaryRemaining = result.Quota.Secondary?.RemainingPercent,
        });
        Console.WriteLine(JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true }));
        return results.All(result => result.IsUsable && result.Quota.Status == QuotaStatus.Available) ? 0 : 1;
    }

    private static async Task<int> InspectAnalyticsLiveAsync(string authDirectory)
    {
        var accounts = new AuthAccountCatalog(authDirectory).Load();
        var endDate = DateOnly.FromDateTime(DateTime.Today);
        var startDate = endDate.AddDays(-6);
        using var analyzer = new TokenCostAnalyzerClient(authDirectory);
        var results = await Task.WhenAll(accounts.Select(async account => new
        {
            account.Slot,
            account.IsCurrent,
            account.IsUsable,
            Analysis = account.IsUsable
                ? await analyzer.QueryAsync(account.Slot, startDate, endDate)
                : TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, account.ErrorMessage ?? "invalid"),
        }));
        var safe = results.Select(result => new
        {
            result.Slot,
            result.IsCurrent,
            result.IsUsable,
            Status = result.Analysis.Status.ToString(),
            result.Analysis.TotalTokens,
            result.Analysis.ComputedCredits,
            result.Analysis.ApiStandardUsd,
            result.Analysis.ExactModelTokenCoverage,
            ModelCount = result.Analysis.Models.Count,
            result.Analysis.ErrorMessage,
        });
        Console.WriteLine(JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true }));
        return results.Any(result => result.Analysis.Status == TokenCostStatus.Available) ? 0 : 1;
    }

    private static Task TestCatalogParsingAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-active", "测试甲", "alpha@example.test", "team", 3_600, "研发 Team");
        fixture.WriteAccount("auth.json0", "acct-plus", "测试乙", "beta@example.test", "plus", -60);

        var accounts = new AuthAccountCatalog(fixture.Path).Load();
        Equal(2, accounts.Count);
        Equal("测试甲", accounts[0].DisplayName);
        Equal("alpha@example.test", accounts[0].Email);
        Equal("Team", accounts[0].Plan);
        Equal("研发 Team", accounts[0].WorkspaceName);
        True(accounts[0].IsCurrent, "active slot should be current");
        Equal("Plus", accounts[1].Plan);
        True(accounts[1].TokenExpiresAt < DateTimeOffset.Now, "expired token should be reported as metadata");
        True(typeof(AccountSummary).GetProperties().All(property => !property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) || property.Name == nameof(AccountSummary.TokenExpiresAt)), "DTO must not expose token strings");
        True(accounts.All(account => !account.ToString().Contains("fixture-access", StringComparison.Ordinal)), "DTO string must not contain tokens");
        return Task.CompletedTask;
    }

    private static Task TestInvalidAndDuplicateAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        fixture.WriteAccount("auth.json0", "acct-a", "测试甲副本", "copy@example.test", "team");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "auth.json1"), "{broken", Encoding.UTF8);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "auth.json-backup"), "ignored", Encoding.UTF8);

        var accounts = new AuthAccountCatalog(fixture.Path).Load();
        Equal(3, accounts.Count);
        True(accounts[0].IsUsable, "first account should remain usable");
        False(accounts[1].IsUsable, "duplicate account should be blocked");
        Contains(accounts[1].ErrorMessage, "重复账号");
        False(accounts[2].IsUsable, "malformed JSON should be blocked");
        return Task.CompletedTask;
    }

    private static Task TestPathValidationAsync()
    {
        using var fixture = new FixtureDirectory();
        var catalog = new AuthAccountCatalog(fixture.Path);
        Throws<ArgumentException>(() => catalog.ResolveSlot("../auth.json"));
        Throws<ArgumentException>(() => catalog.ResolveSlot("auth.json00"));
        Throws<ArgumentException>(() => catalog.ResolveSlot("auth.json.tmp"));
        Equal(System.IO.Path.Combine(fixture.Path, "auth.json12"), catalog.ResolveSlot("auth.json12"));
        return Task.CompletedTask;
    }

    private static Task TestDeleteSavedAccountAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-current", "当前账号", "current@example.test", "team");
        fixture.WriteAccount("auth.json0", "acct-expired", "失效账号", "expired@example.test", "team", -3_600);
        var brokenPath = System.IO.Path.Combine(fixture.Path, "auth.json1");
        File.WriteAllText(brokenPath, "{broken-json", Encoding.UTF8);

        var catalog = new AuthAccountCatalog(fixture.Path);
        var snapshots = catalog.Load();
        var expired = snapshots.Single(account => account.Slot == "auth.json0");
        var broken = snapshots.Single(account => account.Slot == "auth.json1");
        False(broken.IsUsable, "broken JSON should still be listed as deletable metadata");

        var service = new AuthSwitchService(fixture.Path);
        service.DeleteSavedAccount(broken.Slot, broken.ContentHash);
        False(File.Exists(brokenPath), "broken JSON should be deleted without parsing it");

        Throws<InvalidOperationException>(() =>
            service.DeleteSavedAccount(expired.Slot, new string('0', 64)));
        True(File.Exists(System.IO.Path.Combine(fixture.Path, expired.Slot)), "hash mismatch must preserve the account file");

        service.DeleteSavedAccount(expired.Slot, expired.ContentHash);
        False(File.Exists(System.IO.Path.Combine(fixture.Path, expired.Slot)), "expired account should be deleted directly");
        Throws<InvalidOperationException>(() =>
            service.DeleteSavedAccount(AuthAccountCatalog.CurrentSlot, snapshots[0].ContentHash));
        True(File.Exists(System.IO.Path.Combine(fixture.Path, AuthAccountCatalog.CurrentSlot)), "current account must never be deleted");
        return Task.CompletedTask;
    }

    private static async Task TestQuotaSuccessAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        var resetAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var body = $$"""
        {
          "plan_type": "team",
          "rate_limit": {
            "primary_window": { "used_percent": 25.5, "limit_window_seconds": 18000, "reset_at": {{resetAt}} },
            "secondary_window": { "used_percent": 70, "limit_window_seconds": 604800, "reset_at": {{resetAt}} }
          },
          "credits": { "balance": 12.75 }
        }
        """;
        using var client = new QuotaClient(fixture.Path, new StaticHandler(HttpStatusCode.OK, body));
        var quota = await client.QueryAsync("auth.json");

        Equal(QuotaStatus.Available, quota.Status);
        Near(74.5, quota.Primary?.RemainingPercent ?? -1, 0.001);
        Near(30, quota.Secondary?.RemainingPercent ?? -1, 0.001);
        Equal(12.75m, quota.CreditBalance);
    }

    private static async Task TestQuotaAuthFailuresAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");

        using (var unauthorized = new QuotaClient(fixture.Path, new StaticHandler(HttpStatusCode.Unauthorized, "{}")))
        {
            Equal(QuotaStatus.Unauthorized, (await unauthorized.QueryAsync("auth.json")).Status);
        }

        using (var forbidden = new QuotaClient(fixture.Path, new StaticHandler(HttpStatusCode.Forbidden, "{}")))
        {
            Equal(QuotaStatus.Forbidden, (await forbidden.QueryAsync("auth.json")).Status);
        }
    }

    private static async Task TestQuotaTimeoutAndInvalidAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");

        using (var timeout = new QuotaClient(
                   fixture.Path,
                   new DelayedHandler(TimeSpan.FromSeconds(2)),
                   timeout: TimeSpan.FromMilliseconds(40)))
        {
            Equal(QuotaStatus.Timeout, (await timeout.QueryAsync("auth.json")).Status);
        }

        using (var invalid = new QuotaClient(fixture.Path, new StaticHandler(HttpStatusCode.OK, "not-json")))
        {
            Equal(QuotaStatus.Error, (await invalid.QueryAsync("auth.json")).Status);
        }
    }

    private static async Task TestWorkspaceNameSuccessAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-target", "个人姓名", "a@example.test", "team");
        const string body = """
        {
          "accounts": [
            {
              "id": "acct-other",
              "name": "不能误用的 Team",
              "structure": "workspace",
              "plan_type": "business"
            },
            {
              "id": "acct-target",
              "name": "星河研发 Team",
              "structure": "workspace",
              "plan_type": "business"
            }
          ]
        }
        """;

        using var client = new WorkspaceNameClient(fixture.Path, new WorkspaceStaticHandler(HttpStatusCode.OK, body));
        var result = await client.QueryAsync("auth.json");

        Equal(WorkspaceNameStatus.Available, result.Status);
        Equal("星河研发 Team", result.Name);
        True(typeof(WorkspaceNameInfo).GetProperties().All(property => !property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)), "workspace DTO must not expose tokens");
    }

    private static async Task TestWorkspaceNameFailuresAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-team", "个人姓名", "team@example.test", "team");
        fixture.WriteAccount("auth.json0", "acct-plus", "个人账号", "plus@example.test", "plus");

        using (var forbidden = new WorkspaceNameClient(fixture.Path, new WorkspaceStaticHandler(HttpStatusCode.Forbidden, "{}")))
        {
            Equal(WorkspaceNameStatus.Forbidden, (await forbidden.QueryAsync("auth.json")).Status);
        }

        using (var missing = new WorkspaceNameClient(fixture.Path, new WorkspaceStaticHandler(HttpStatusCode.OK, "{\"accounts\":{}}")))
        {
            Equal(WorkspaceNameStatus.Error, (await missing.QueryAsync("auth.json")).Status);
        }

        using (var personal = new WorkspaceNameClient(fixture.Path, new ThrowingHandler()))
        {
            Equal(WorkspaceNameStatus.NotRequired, (await personal.QueryAsync("auth.json0")).Status);
        }
    }

    private static Task TestTokenCostExactAsync()
    {
        var date = new DateOnly(2026, 8, 20);
        const string counts = """
        {
          "data": [
            {
              "date": "2026-08-20",
              "totals": {
                "credits": 0,
                "turns": 4,
                "uncached_text_input_tokens": 1000000,
                "cached_text_input_tokens": 1000000,
                "text_output_tokens": 1000000,
                "text_total_tokens": 3000000
              },
              "models": [
                {
                  "model": "gpt-5.6-terra",
                  "speed": "fast",
                  "uncached_text_input_tokens": 1000000,
                  "cached_text_input_tokens": 1000000,
                  "text_output_tokens": 1000000,
                  "text_total_tokens": 3000000
                }
              ]
            }
          ]
        }
        """;
        const string breakdown = """
        {
          "units": "percent",
          "data": [
            {
              "date": "2026-08-20",
              "models": [
                { "model": "gpt-5.6-terra", "speed": "fast", "credits": 100 }
              ],
              "product_surface_usage_values": { "desktop_app": 100 }
            }
          ]
        }
        """;

        var result = TokenCostAnalyzerClient.AnalyzeForTest(counts, breakdown, date, date);
        Equal(TokenCostStatus.Available, result.Status);
        Equal(3_000_000m, result.TotalTokens);
        Near(1_109.375, (double)result.ComputedCredits, 0.0001);
        Near(14.2, (double)result.ApiStandardUsd, 0.0001);
        Near(28.4, (double)result.ApiMatchedSpeedUsd, 0.0001);
        Near(0.5, (double)result.CacheHitRate, 0.0001);
        Equal(1m, result.ExactModelTokenCoverage);
        True(result.Models.Single().IsExact, "workspace model tokens should be exact");
        return Task.CompletedTask;
    }

    private static async Task TestTokenCostFallbackAndAuthAsync()
    {
        var date = new DateOnly(2026, 8, 20);
        const string counts = """
        {
          "data": [
            {
              "date": "2026-08-20",
              "totals": {
                "credits": 0,
                "turns": 1,
                "uncached_text_input_tokens": 1000,
                "cached_text_input_tokens": 2000,
                "text_output_tokens": 500,
                "text_total_tokens": 3500
              },
              "models": []
            }
          ]
        }
        """;
        var fallback = TokenCostAnalyzerClient.AnalyzeForTest(counts, null, date, date);
        Equal(0m, fallback.ExactModelTokenCoverage);
        False(fallback.Models.Single().IsExact, "total-only data must be marked as estimated");
        Contains(fallback.Warning, "回退估算");

        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        using var client = new TokenCostAnalyzerClient(
            fixture.Path,
            new StaticHandler(HttpStatusCode.Unauthorized, "{}"),
            new Uri("https://chatgpt.example.test/"));
        var unauthorized = await client.QueryAsync("auth.json", date, date);
        Equal(TokenCostStatus.Unauthorized, unauthorized.Status);
    }

    private static async Task TestChatGptRestartAsync()
    {
        True(
            ChatGptRestartService.IsOwnedExecutable(
                "ChatGPT",
                @"C:\Program Files\WindowsApps\OpenAI.Codex_26.818.2441.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
            "Store ChatGPT process should be targeted");
        True(
            ChatGptRestartService.IsOwnedExecutable(
                "codex",
                @"C:\Program Files\WindowsApps\OpenAI.Codex_26.818.2441.0_x64__2p2nqsd0c76g0\app\resources\codex.exe"),
            "packaged Codex child should be targeted");
        False(
            ChatGptRestartService.IsOwnedExecutable(
                "codex",
                @"C:\Users\test\.vscode\extensions\openai.chatgpt\bin\codex.exe"),
            "VS Code Codex process must not be targeted");
        False(
            ChatGptRestartService.IsOwnedExecutable(
                "ChatGPT",
                @"C:\Tools\ChatGPT.exe"),
            "unrelated executable with the same name must not be targeted");

        var runtime = new FakeChatGptRuntime(
        [
            new OwnedProcess(10, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_x\app\ChatGPT.exe"),
            new OwnedProcess(11, "codex", @"C:\Program Files\WindowsApps\OpenAI.Codex_x\app\resources\codex.exe"),
        ]);
        var service = new ChatGptRestartService(
            runtime,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var result = await service.RestartAsync();

        True(result.Success, "restart should be verified by a new ChatGPT process");
        Equal(2, result.ClosedProcessCount);
        Equal(2, result.ForcedProcessCount);
        True(runtime.Activated, "Store app activation should run after old processes exit");
        Equal(ChatGptRestartService.AppUserModelId, runtime.ActivatedAppId);
    }

    private static Task TestSwitchAndHashGuardAsync()
    {
        using var fixture = CreateSwitchFixture();
        var catalog = new AuthAccountCatalog(fixture.Path);
        var before = catalog.Load();
        var activeBefore = before.Single(account => account.Slot == "auth.json");
        var targetBefore = before.Single(account => account.Slot == "auth.json0");
        var service = new AuthSwitchService(fixture.Path);

        Throws<InvalidOperationException>(() => service.Switch("auth.json0", new string('0', 64)));
        Equal(activeBefore.ContentHash, new AuthAccountCatalog(fixture.Path).Load().Single(account => account.Slot == "auth.json").ContentHash);

        service.Switch("auth.json0", targetBefore.ContentHash);
        var after = catalog.Load();
        Equal("acct-b", after.Single(account => account.Slot == "auth.json").AccountId);
        Equal("acct-a", after.Single(account => account.Slot == "auth.json0").AccountId);
        False(File.Exists(System.IO.Path.Combine(fixture.Path, ".account-switcher-transaction.json")), "journal should be removed after success");
        return Task.CompletedTask;
    }

    private static Task TestCrashRecoveryAsync()
    {
        foreach (var fault in Enum.GetValues<SwitchFaultPoint>())
        {
            using var fixture = CreateSwitchFixture();
            var target = new AuthAccountCatalog(fixture.Path).Load().Single(account => account.Slot == "auth.json0");
            var service = new AuthSwitchService(fixture.Path);
            Throws<SimulatedCrashException>(() => service.SwitchForTest("auth.json0", target.ContentHash, fault, recoverOnFailure: false));

            True(new AuthSwitchService(fixture.Path).RecoverPending(), $"recovery should run for {fault}");
            var recovered = new AuthAccountCatalog(fixture.Path).Load();
            var expectedActive = fault is SwitchFaultPoint.AfterTargetPromoted or SwitchFaultPoint.AfterSwapCompleted
                ? "acct-b"
                : "acct-a";
            var expectedTarget = expectedActive == "acct-b" ? "acct-a" : "acct-b";
            Equal(expectedActive, recovered.Single(account => account.Slot == "auth.json").AccountId);
            Equal(expectedTarget, recovered.Single(account => account.Slot == "auth.json0").AccountId);
            False(File.Exists(System.IO.Path.Combine(fixture.Path, ".account-switcher-transaction.json")), $"journal should be removed for {fault}");
            False(Directory.EnumerateFiles(fixture.Path, ".account-switcher-*.tmp").Any(), $"temp should be removed for {fault}");
        }

        return Task.CompletedTask;
    }

    private static async Task TestLoginBrandNewAccountAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        fixture.WriteAccount("auth.json0", "acct-c", "测试丙", "c@example.test", "plus");
        var activeHash = new AuthAccountCatalog(fixture.Path).Load().Single(account => account.IsCurrent).ContentHash;
        var service = new AccountLoginService(fixture.Path);

        var result = await service.LoginNewAccountAsync((temporaryHome, _) =>
        {
            Equal(activeHash, new AuthAccountCatalog(fixture.Path).Load().Single(account => account.IsCurrent).ContentHash);
            fixture.WriteAccountTo(temporaryHome, "auth.json", "acct-b", "测试乙", "b@example.test", "pro");
            return Task.FromResult(0);
        });

        var accounts = new AuthAccountCatalog(fixture.Path).Load();
        Equal("acct-b", accounts.Single(account => account.Slot == "auth.json").AccountId);
        Equal("acct-c", accounts.Single(account => account.Slot == "auth.json0").AccountId);
        Equal("acct-a", accounts.Single(account => account.Slot == "auth.json1").AccountId);
        Equal("auth.json1", result.ArchivedPreviousSlot);
        False(result.ReusedExistingSlot, "brand-new login should use a new slot");
        AssertNoLoginArtifacts(fixture.Path);
    }

    private static async Task TestLoginExistingAccountAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        fixture.WriteAccount("auth.json0", "acct-b", "测试乙旧凭据", "b@example.test", "plus");
        var service = new AccountLoginService(fixture.Path);

        var result = await service.LoginNewAccountAsync((temporaryHome, _) =>
        {
            fixture.WriteAccountTo(temporaryHome, "auth.json", "acct-b", "测试乙新凭据", "b@example.test", "plus");
            return Task.FromResult(0);
        });

        var accounts = new AuthAccountCatalog(fixture.Path).Load();
        Equal(2, accounts.Count);
        Equal("测试乙新凭据", accounts.Single(account => account.Slot == "auth.json").DisplayName);
        Equal("acct-a", accounts.Single(account => account.Slot == "auth.json0").AccountId);
        True(result.ReusedExistingSlot, "existing account should reuse its old slot for previous current account");
        Equal("auth.json0", result.ArchivedPreviousSlot);
        AssertNoLoginArtifacts(fixture.Path);
    }

    private static async Task TestLoginCurrentAndFailureAsync()
    {
        using var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        var service = new AccountLoginService(fixture.Path);

        var refreshed = await service.LoginNewAccountAsync((temporaryHome, _) =>
        {
            fixture.WriteAccountTo(temporaryHome, "auth.json", "acct-a", "测试甲已刷新", "a@example.test", "team");
            return Task.FromResult(0);
        });
        True(refreshed.RefreshedCurrentAccount, "same account login should refresh current credentials");
        Equal("测试甲已刷新", new AuthAccountCatalog(fixture.Path).Load().Single().DisplayName);

        var hashBeforeFailure = new AuthAccountCatalog(fixture.Path).Load().Single().ContentHash;
        await ThrowsAsync<InvalidOperationException>(() => service.LoginNewAccountAsync((_, _) => Task.FromResult(1)));
        Equal(hashBeforeFailure, new AuthAccountCatalog(fixture.Path).Load().Single().ContentHash);

        await ThrowsAsync<InvalidDataException>(() => service.LoginNewAccountAsync((temporaryHome, _) =>
        {
            File.WriteAllText(System.IO.Path.Combine(temporaryHome, "auth.json"), "{broken", Encoding.UTF8);
            return Task.FromResult(0);
        }));
        Equal(hashBeforeFailure, new AuthAccountCatalog(fixture.Path).Load().Single().ContentHash);
        AssertNoLoginArtifacts(fixture.Path);
    }

    private static async Task TestLoginCrashRecoveryAsync()
    {
        foreach (var fault in Enum.GetValues<LoginFaultPoint>())
        {
            using var fixture = new FixtureDirectory();
            fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
            fixture.WriteAccount("auth.json0", "acct-b", "测试乙旧凭据", "b@example.test", "plus");
            var service = new AccountLoginService(fixture.Path);

            await ThrowsAsync<LoginSimulatedCrashException>(() => service.LoginForTestAsync(
                (temporaryHome, _) =>
                {
                    fixture.WriteAccountTo(temporaryHome, "auth.json", "acct-b", "测试乙新凭据", "b@example.test", "plus");
                    return Task.FromResult(0);
                },
                fault,
                recoverOnFailure: false));

            True(new AccountLoginService(fixture.Path).RecoverPending(), $"login recovery should run for {fault}");
            var recovered = new AuthAccountCatalog(fixture.Path).Load();
            Equal("acct-b", recovered.Single(account => account.Slot == "auth.json").AccountId);
            Equal("acct-a", recovered.Single(account => account.Slot == "auth.json0").AccountId);
            AssertNoLoginArtifacts(fixture.Path);
        }
    }

    private static void AssertNoLoginArtifacts(string directory)
    {
        False(
            Directory.EnumerateFileSystemEntries(directory, ".account-login-*").Any(),
            "login transaction artifacts should be removed");
    }

    private static FixtureDirectory CreateSwitchFixture()
    {
        var fixture = new FixtureDirectory();
        fixture.WriteAccount("auth.json", "acct-a", "测试甲", "a@example.test", "team");
        fixture.WriteAccount("auth.json0", "acct-b", "测试乙", "b@example.test", "plus");
        return fixture;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
        }
    }

    private static void Near(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Contains(string? value, string expected)
    {
        if (value is null || !value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{value}' does not contain '{expected}'");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }

    private sealed class FixtureDirectory : IDisposable
    {
        public FixtureDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexAccountSwitcherTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteAccount(
            string slot,
            string accountId,
            string name,
            string email,
            string plan,
            int expiryOffsetSeconds = 3_600,
            string? workspaceName = null)
        {
            WriteAccountTo(Path, slot, accountId, name, email, plan, expiryOffsetSeconds, workspaceName);
        }

        public void WriteAccountTo(
            string directory,
            string slot,
            string accountId,
            string name,
            string email,
            string plan,
            int expiryOffsetSeconds = 3_600,
            string? workspaceName = null)
        {
            Directory.CreateDirectory(directory);
            var idToken = Jwt(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["email"] = email,
                ["exp"] = DateTimeOffset.UtcNow.AddSeconds(expiryOffsetSeconds).ToUnixTimeSeconds(),
            });
            var authClaims = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = accountId,
                ["chatgpt_plan_type"] = plan,
            };
            if (!string.IsNullOrWhiteSpace(workspaceName))
            {
                authClaims["organizations"] = new[]
                {
                    new { id = "unrelated-account", title = "不能误用的 Team", role = "member" },
                    new { id = accountId, title = workspaceName, role = "member" },
                };
            }

            var accessToken = Jwt(new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] = authClaims,
                ["exp"] = DateTimeOffset.UtcNow.AddSeconds(expiryOffsetSeconds).ToUnixTimeSeconds(),
                ["fixture-access-marker"] = "fixture-access",
            });
            var root = new
            {
                auth_mode = "chatgpt",
                tokens = new
                {
                    id_token = idToken,
                    access_token = accessToken,
                    refresh_token = "fixture-refresh-secret",
                    account_id = accountId,
                },
                last_refresh = DateTimeOffset.UtcNow,
            };
            File.WriteAllText(
                System.IO.Path.Combine(directory, slot),
                JsonSerializer.Serialize(root),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private static string Jwt(Dictionary<string, object?> payload)
        {
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }));
            var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
            return $"{header}.{body}.fixture";
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class StaticHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            True(request.Headers.Authorization?.Scheme == "Bearer", "bearer header missing");
            True(request.Headers.Contains("ChatGPT-Account-Id"), "account header missing");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class WorkspaceStaticHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            True(request.Headers.Authorization?.Scheme == "Bearer", "bearer header missing");
            True(request.Headers.Contains("ChatGPT-Account-Id"), "workspace account header missing");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("personal plans must not call the Workspace endpoint");
    }

    private sealed class FakeChatGptRuntime(IEnumerable<OwnedProcess> initialProcesses) : IChatGptRuntime
    {
        private readonly List<OwnedProcess> _processes = [.. initialProcesses];

        public bool Activated { get; private set; }

        public string? ActivatedAppId { get; private set; }

        public IReadOnlyList<OwnedProcess> ListOwnedProcesses() => [.. _processes];

        public bool RequestClose(int processId) => _processes.Any(process => process.Id == processId);

        public bool ForceTerminate(int processId)
        {
            var removed = _processes.RemoveAll(process => process.Id == processId);
            return removed > 0;
        }

        public void Activate(string appUserModelId)
        {
            Activated = true;
            ActivatedAppId = appUserModelId;
            _processes.Add(new OwnedProcess(20, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_x\app\ChatGPT.exe"));
        }
    }
}
