using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

public sealed class WorkspaceNameClient : IDisposable
{
    private static readonly Uri DefaultAccountsEndpoint =
        new("https://chatgpt.com/backend-api/wham/accounts/check");

    private readonly AuthAccountCatalog _catalog;
    private readonly HttpClient _httpClient;
    private readonly Uri _accountsEndpoint;
    private readonly TimeSpan _timeout;

    public WorkspaceNameClient(
        string authDirectory,
        HttpMessageHandler? handler = null,
        Uri? accountsEndpoint = null,
        TimeSpan? timeout = null)
    {
        _catalog = new AuthAccountCatalog(authDirectory);
        _httpClient = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAccountSwitcher/1.0");
        _accountsEndpoint = accountsEndpoint ?? DefaultAccountsEndpoint;
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
    }

    public static bool RequiresLookup(string? plan) => plan?.Trim().ToLowerInvariant() is
        "team" or "business" or "enterprise";

    public async Task<WorkspaceNameInfo> QueryAsync(
        string slot,
        CancellationToken cancellationToken = default)
    {
        AuthCredentials credentials;
        try
        {
            var path = _catalog.ResolveSlot(slot);
            AuthAccountCatalog.EnsureSafeFile(path);
            credentials = AuthFileParser.Parse(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, exception.Message);
        }

        if (!RequiresLookup(credentials.Plan))
        {
            return WorkspaceNameInfo.NotRequired();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _accountsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Unauthorized, "登录已过期，无法读取 Team 名称");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Forbidden, "当前账号无权读取 Team 名称");
            }

            if (!response.IsSuccessStatusCode)
            {
                return WorkspaceNameInfo.Failure(
                    WorkspaceNameStatus.Error,
                    $"Team 名称服务返回 HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeoutSource.Token).ConfigureAwait(false);
            return Parse(document.RootElement, credentials.AccountId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Timeout, "Team 名称查询超时");
        }
        catch (HttpRequestException)
        {
            return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, "无法连接 Team 名称服务");
        }
        catch (JsonException)
        {
            return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, "Team 名称服务返回了无效数据");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    internal static WorkspaceNameInfo Parse(JsonElement root, string accountId)
    {
        if (!root.TryGetProperty("accounts", out var accounts))
        {
            return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, "账号响应中没有 Workspace 列表");
        }

        if (accounts.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in accounts.EnumerateArray())
            {
                var matched = ParseAccount(account, accountId, ReadString(account, "id"));
                if (matched is not null)
                {
                    return matched;
                }
            }
        }
        else if (accounts.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in accounts.EnumerateObject())
            {
                var account = item.Value;
                if (item.Value.ValueKind == JsonValueKind.Object &&
                    item.Value.TryGetProperty("account", out var nestedAccount))
                {
                    account = nestedAccount;
                }

                var responseId = ReadString(account, "account_id") ?? ReadString(account, "id") ?? item.Name;
                var matched = ParseAccount(account, accountId, responseId);
                if (matched is not null)
                {
                    return matched;
                }
            }
        }

        return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, "未找到与认证账号匹配的 Team");
    }

    private static WorkspaceNameInfo? ParseAccount(JsonElement account, string accountId, string? responseAccountId)
    {
        if (account.ValueKind != JsonValueKind.Object ||
            !string.Equals(responseAccountId, accountId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var structure = ReadString(account, "structure");
        if (!string.Equals(structure, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, "匹配账号不是 Team Workspace");
        }

        var name = ReadString(account, "name");
        return string.IsNullOrWhiteSpace(name)
            ? WorkspaceNameInfo.Failure(WorkspaceNameStatus.Error, "匹配的 Team 没有名称")
            : new WorkspaceNameInfo(WorkspaceNameStatus.Available, name.Trim(), null);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
