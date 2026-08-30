using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

public sealed class QuotaClient : IDisposable
{
    private static readonly Uri DefaultUsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private readonly AuthAccountCatalog _catalog;
    private readonly HttpClient _httpClient;
    private readonly Uri _usageEndpoint;
    private readonly TimeSpan _timeout;
    private readonly bool _disposeClient;

    public QuotaClient(
        string authDirectory,
        HttpMessageHandler? handler = null,
        Uri? usageEndpoint = null,
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
        _usageEndpoint = usageEndpoint ?? DefaultUsageEndpoint;
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
        _disposeClient = true;
    }

    public async Task<QuotaInfo> QueryAsync(string slot, CancellationToken cancellationToken = default)
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
            return QuotaInfo.Failure(QuotaStatus.Error, exception.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _usageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        request.Headers.TryAddWithoutValidation("OAI-App-Brand", "chatgpt");
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
                return QuotaInfo.Failure(QuotaStatus.Unauthorized, "登录已过期，请先切换后由官方客户端刷新");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return QuotaInfo.Failure(QuotaStatus.Forbidden, "当前账号无权查询额度");
            }

            if (!response.IsSuccessStatusCode)
            {
                return QuotaInfo.Failure(QuotaStatus.Error, $"额度服务返回 HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutSource.Token).ConfigureAwait(false);
            return ParseUsage(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return QuotaInfo.Failure(QuotaStatus.Timeout, "额度查询超时");
        }
        catch (HttpRequestException)
        {
            return QuotaInfo.Failure(QuotaStatus.Error, "无法连接额度服务");
        }
        catch (JsonException)
        {
            return QuotaInfo.Failure(QuotaStatus.Error, "额度服务返回了无效数据");
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    private static QuotaInfo ParseUsage(JsonElement root)
    {
        var plan = ReadString(root, "plan_type");
        UsageWindow? primary = null;
        UsageWindow? secondary = null;

        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            if (rateLimit.TryGetProperty("primary_window", out var primaryElement))
            {
                primary = ParseWindow(primaryElement);
            }

            if (rateLimit.TryGetProperty("secondary_window", out var secondaryElement))
            {
                secondary = ParseWindow(secondaryElement);
            }
        }

        decimal? credits = null;
        if (root.TryGetProperty("credits", out var creditElement) && creditElement.ValueKind == JsonValueKind.Object)
        {
            credits = ReadDecimal(creditElement, "balance");
        }

        if (primary is null && secondary is null && credits is null)
        {
            return QuotaInfo.Failure(QuotaStatus.Error, "额度响应中没有可展示的窗口");
        }

        return new QuotaInfo(
            QuotaStatus.Available,
            plan,
            primary,
            secondary,
            credits,
            DateTimeOffset.Now,
            null);
    }

    private static UsageWindow? ParseWindow(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var usedPercent = ReadDouble(element, "used_percent");
        if (usedPercent is null)
        {
            return null;
        }

        var windowSeconds = ReadInt(element, "limit_window_seconds");
        var resetAtSeconds = ReadLong(element, "reset_at");
        DateTimeOffset? resetAt = null;
        if (resetAtSeconds is not null)
        {
            try
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds(resetAtSeconds.Value).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                resetAt = null;
            }
        }

        return new UsageWindow(Math.Clamp(usedPercent.Value, 0d, 100d), windowSeconds, resetAt);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static decimal? ReadDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
}
