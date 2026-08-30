using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Native, token-safe port of the calculation path in
/// Codex Token API Cost Analyzer v4.4.0 (pricing verified 2026-08-12).
/// </summary>
public sealed class TokenCostAnalyzerClient : IDisposable
{
    public const string AnalyzerVersion = "4.4.0-native";
    public const string PricingVerifiedDate = "2026-08-12";
    private const decimal Epsilon = 0.000000001m;
    private static readonly Uri DefaultBaseUri = new("https://chatgpt.com/");
    private readonly AuthAccountCatalog _catalog;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly TimeSpan _timeout;

    public TokenCostAnalyzerClient(
        string authDirectory,
        HttpMessageHandler? handler = null,
        Uri? baseUri = null,
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAccountSwitcher/1.1");
        _baseUri = baseUri ?? DefaultBaseUri;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<TokenCostAnalysis> QueryAsync(
        string slot,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, "开始日期不能晚于结束日期");
        }

        if (endDate.DayNumber - startDate.DayNumber + 1 > 366)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, "单次最多查询 366 天");
        }

        AuthCredentials credentials;
        try
        {
            var path = _catalog.ResolveSlot(slot);
            AuthAccountCatalog.EnsureSafeFile(path);
            credentials = AuthFileParser.Parse(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, exception.Message);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            var query = BuildQuery(startDate, endDate);
            var countsTask = GetJsonAsync(
                new Uri(_baseUri, $"backend-api/wham/analytics/daily-workspace-usage-counts?{query}"),
                credentials,
                timeoutSource.Token);
            var breakdownTask = GetJsonAsync(
                new Uri(_baseUri, $"backend-api/wham/usage/daily-token-usage-breakdown?{query}"),
                credentials,
                timeoutSource.Token);

            JsonDocument? countsDocument = null;
            JsonDocument? breakdownDocument = null;
            Exception? breakdownError = null;

            try
            {
                countsDocument = await countsTask.ConfigureAwait(false);
            }
            catch (AnalyticsHttpException exception)
            {
                return HttpFailure(exception, startDate, endDate);
            }

            try
            {
                breakdownDocument = await breakdownTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is AnalyticsHttpException or HttpRequestException or JsonException)
            {
                breakdownError = exception;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                breakdownError = new TimeoutException("Breakdown 接口查询超时");
            }

            using (countsDocument)
            using (breakdownDocument)
            {
                return Analyze(
                    countsDocument.RootElement,
                    breakdownDocument?.RootElement,
                    startDate,
                    endDate,
                    breakdownError?.Message);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Timeout, startDate, endDate, "精确分析查询超时");
        }
        catch (HttpRequestException)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, "无法连接 Analytics 服务");
        }
        catch (JsonException)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, "Analytics 服务返回了无效 JSON");
        }
        catch (InvalidDataException exception)
        {
            return TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, exception.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    internal static TokenCostAnalysis AnalyzeForTest(
        string countsJson,
        string? breakdownJson,
        DateOnly startDate,
        DateOnly endDate)
    {
        using var counts = JsonDocument.Parse(countsJson);
        using var breakdown = breakdownJson is null ? null : JsonDocument.Parse(breakdownJson);
        return Analyze(counts.RootElement, breakdown?.RootElement, startDate, endDate, null);
    }

    private static string BuildQuery(DateOnly startDate, DateOnly endDate) =>
        $"start_date={startDate:yyyy-MM-dd}&end_date={endDate.AddDays(1):yyyy-MM-dd}&group_by=day";

    private async Task<JsonDocument> GetJsonAsync(Uri uri, AuthCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        request.Headers.TryAddWithoutValidation("OAI-App-Brand", "chatgpt");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AnalyticsHttpException(response.StatusCode, $"Analytics 服务返回 HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static TokenCostAnalysis HttpFailure(
        AnalyticsHttpException exception,
        DateOnly startDate,
        DateOnly endDate) =>
        exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized => TokenCostAnalysis.Failure(
                TokenCostStatus.Unauthorized,
                startDate,
                endDate,
                "登录已过期，请切换后由官方客户端刷新"),
            HttpStatusCode.Forbidden => TokenCostAnalysis.Failure(
                TokenCostStatus.Forbidden,
                startDate,
                endDate,
                "当前账号无权读取 Workspace Analytics"),
            _ => TokenCostAnalysis.Failure(TokenCostStatus.Error, startDate, endDate, exception.Message),
        };

    private static TokenCostAnalysis Analyze(
        JsonElement countsPayload,
        JsonElement? breakdownPayload,
        DateOnly startDate,
        DateOnly endDate,
        string? breakdownError)
    {
        var countRows = ExtractRows(countsPayload)
            .Select(ParseCountRow)
            .Where(row => row is not null && row.Date >= startDate && row.Date <= endDate)
            .Cast<CountRow>()
            .ToDictionary(row => row.Date, row => row);
        if (countRows.Count == 0)
        {
            throw new InvalidDataException("Workspace Analytics 没有返回所选日期的绝对 Token 数据");
        }

        var breakdownRows = breakdownPayload is JsonElement payload
            ? ExtractRows(payload)
                .Select(ParseBreakdownRow)
                .Where(row => row is not null)
                .Cast<BreakdownRow>()
                .ToDictionary(row => row.Date, row => row)
            : [];

        var allocations = new List<ModelAllocation>();
        decimal uncached = 0;
        decimal cached = 0;
        decimal output = 0;
        decimal totalTokens = 0;
        decimal reportedCredits = 0;
        decimal turns = 0;
        var activeDays = 0;

        foreach (var row in countRows.Values.OrderBy(row => row.Date))
        {
            if (row.Tokens.Total <= Epsilon && row.ReportedCredits <= Epsilon && row.Turns <= Epsilon)
            {
                continue;
            }

            activeDays++;
            uncached += row.Tokens.Uncached;
            cached += row.Tokens.Cached;
            output += row.Tokens.Output;
            totalTokens += row.Tokens.Total;
            reportedCredits += row.ReportedCredits;
            turns += row.Turns;

            var daily = new List<ModelAllocation>();
            var exactParts = new TokenParts();
            foreach (var model in row.Models.Where(model => model.HasTokenDetails && model.Tokens.Total > Epsilon))
            {
                daily.Add(AllocateFromTokens(model.Model, model.Speed, model.Tokens, row.Date, true));
                exactParts = exactParts.Add(model.Tokens);
            }

            if (daily.Count > 0)
            {
                var residual = row.Tokens.SubtractFloor(exactParts);
                if (residual.Total > Math.Max(1m, row.Tokens.Total * 0.00000001m))
                {
                    daily.Add(AllocateFromTokens(PickFallbackModel(row.Models), "standard", residual, row.Date, false));
                }
            }
            else if (row.ReportedCredits > Epsilon &&
                     breakdownRows.TryGetValue(row.Date, out var breakdown) &&
                     breakdown.Models.Sum(model => model.Value) > Epsilon)
            {
                var scale = row.ReportedCredits / breakdown.Models.Sum(model => model.Value);
                daily.AddRange(breakdown.Models
                    .Where(model => model.Value > Epsilon)
                    .Select(model => AllocateFromCredits(
                        model.Model,
                        model.Speed,
                        model.Value * scale,
                        row.Date)));
            }
            else if (row.Tokens.Total > Epsilon)
            {
                daily.Add(AllocateFromTokens(PickFallbackModel(row.Models), "standard", row.Tokens, row.Date, false));
            }

            allocations.AddRange(daily);
        }

        if (activeDays == 0)
        {
            throw new InvalidDataException("所选日期没有可分析的 Token、Credits 或 Turns");
        }

        var groupedModels = allocations
            .GroupBy(item => $"{item.Model}\u001f{item.Speed}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new TokenCostModelRow(
                    first.Model,
                    first.Rates.Label,
                    first.Speed,
                    group.Sum(item => item.Tokens.Uncached),
                    group.Sum(item => item.Tokens.Cached),
                    group.Sum(item => item.Tokens.Output),
                    group.Sum(item => item.Tokens.Total),
                    group.Sum(item => item.CodexCredits),
                    group.Sum(item => item.ApiStandardUsd),
                    group.Sum(item => item.ApiAtUseUsd),
                    group.Sum(item => item.ApiMatchedSpeedUsd),
                    group.All(item => item.IsExact),
                    first.Rates.IsPriced);
            })
            .OrderByDescending(item => item.TotalTokens)
            .ThenByDescending(item => item.CodexCredits)
            .ToArray();

        var computedCredits = allocations.Sum(item => item.CodexCredits);
        var exactModelTokens = allocations.Where(item => item.IsExact).Sum(item => item.Tokens.Total);
        var pricedTokens = allocations.Where(item => item.IsExact && item.Rates.IsPriced).Sum(item => item.Tokens.Total);
        var inputTokens = uncached + cached;
        var exactCoverage = totalTokens > Epsilon ? Math.Min(1m, exactModelTokens / totalTokens) : 0m;
        var pricedCoverage = totalTokens > Epsilon ? Math.Min(1m, pricedTokens / totalTokens) : 0m;
        var warnings = new List<string>();
        if (breakdownError is not null)
        {
            warnings.Add("Breakdown 接口不可用；模型级绝对 Token 仍可独立计算");
        }
        if (exactCoverage < 0.999999m)
        {
            warnings.Add($"有 {(1m - exactCoverage):P1} Token 使用模型回退估算");
        }
        if (pricedCoverage < exactCoverage)
        {
            warnings.Add("存在未收录费率的模型，其 Token 不计入金额");
        }

        return new TokenCostAnalysis(
            TokenCostStatus.Available,
            startDate,
            endDate,
            uncached,
            cached,
            output,
            totalTokens > Epsilon ? totalTokens : uncached + cached + output,
            computedCredits,
            reportedCredits,
            allocations.Sum(item => item.ApiStandardUsd),
            allocations.Sum(item => item.ApiAtUseUsd),
            allocations.Sum(item => item.ApiMatchedSpeedUsd),
            allocations.Where(item => item.Speed == "fast").Sum(item => item.CodexCredits),
            inputTokens > Epsilon ? cached / inputTokens : 0m,
            exactCoverage,
            pricedCoverage,
            turns,
            activeDays,
            breakdownRows.Count > 0 ? "Workspace Token + Breakdown 交叉校验" : "Workspace 模型级 Token",
            AnalyzerVersion,
            DateTimeOffset.Now,
            groupedModels,
            warnings.Count > 0 ? string.Join("；", warnings) : null,
            null);
    }

    private static IEnumerable<JsonElement> ExtractRows(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray().ToArray();
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var name in new[]
                 {
                     "data", "items", "results", "daily", "daily_usage",
                     "dailyTokenUsageBreakdown", "daily_token_usage_breakdown",
                     "dailyWorkspaceUsageCounts", "daily_workspace_usage_counts", "workspace_usage_counts",
                 })
        {
            if (payload.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
            {
                return candidate.EnumerateArray().ToArray();
            }
        }

        return [];
    }

    private static CountRow? ParseCountRow(JsonElement row)
    {
        if (!TryReadDate(row, out var date))
        {
            return null;
        }

        var totals = row.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : row;
        var models = new List<CountModel>();
        if (row.TryGetProperty("models", out var modelElements) && modelElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in modelElements.EnumerateArray())
            {
                var modelName = ReadString(model, "model") ?? ReadString(model, "name") ?? "UNKNOWN";
                var speed = string.Equals(ReadString(model, "speed"), "fast", StringComparison.OrdinalIgnoreCase)
                    ? "fast"
                    : "standard";
                var tokens = ReadTokenParts(model);
                var hasDetails = HasAnyProperty(
                    model,
                    "text_total_tokens",
                    "uncached_text_input_tokens",
                    "cached_text_input_tokens",
                    "text_output_tokens",
                    "total_tokens",
                    "input_tokens",
                    "output_tokens");
                models.Add(new CountModel(
                    modelName,
                    speed,
                    tokens,
                    hasDetails,
                    ReadDecimal(model, "turns"),
                    ReadDecimal(model, "credits")));
            }
        }

        return new CountRow(
            date,
            ReadTokenParts(totals),
            ReadDecimal(totals, "credits", ReadDecimal(row, "credits")),
            ReadDecimal(totals, "turns", ReadDecimal(row, "turns")),
            models);
    }

    private static BreakdownRow? ParseBreakdownRow(JsonElement row)
    {
        if (!TryReadDate(row, out var date))
        {
            return null;
        }

        var models = new List<BreakdownModel>();
        if (row.TryGetProperty("models", out var elements) && elements.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in elements.EnumerateArray())
            {
                models.Add(new BreakdownModel(
                    ReadString(model, "model") ?? ReadString(model, "name") ?? "UNKNOWN",
                    string.Equals(ReadString(model, "speed"), "fast", StringComparison.OrdinalIgnoreCase)
                        ? "fast"
                        : "standard",
                    ReadDecimal(model, "credits", ReadDecimal(model, "value", ReadDecimal(model, "usage")))));
            }
        }

        return new BreakdownRow(date, models);
    }

    private static ModelAllocation AllocateFromTokens(
        string model,
        string speed,
        TokenParts tokens,
        DateOnly date,
        bool isExact)
    {
        var rates = Pricing.Resolve(model, date);
        var standardCredits = rates.IsPriced ? Cost(tokens, rates.Codex) : 0m;
        var multiplier = speed == "fast" ? rates.CodexFastMultiplier : 1m;
        var currentApi = rates.IsPriced ? Cost(tokens, rates.Api) : 0m;
        var atUseApi = rates.IsPriced ? Cost(tokens, rates.ApiAtUse) : 0m;
        var matchedApi = speed == "fast" && rates.ApiFast is not null
            ? Cost(tokens, rates.ApiFast)
            : currentApi;
        return new ModelAllocation(
            model,
            rates.Label,
            speed,
            tokens,
            standardCredits * multiplier,
            currentApi,
            atUseApi,
            matchedApi,
            isExact,
            rates);
    }

    private static ModelAllocation AllocateFromCredits(
        string model,
        string speed,
        decimal credits,
        DateOnly date)
    {
        var rates = Pricing.Resolve(model, date);
        var multiplier = speed == "fast" ? rates.CodexFastMultiplier : 1m;
        var standardCredits = multiplier > Epsilon ? credits / multiplier : credits;
        var atUse = standardCredits / 25m;
        var current = atUse * rates.CurrentToBilledRatio;
        var matched = speed == "fast" && rates.ApiFast is not null
            ? current * rates.ApiFastMultiplier
            : current;
        return new ModelAllocation(
            model,
            rates.Label,
            speed,
            new TokenParts(),
            credits,
            current,
            atUse,
            matched,
            false,
            rates);
    }

    private static decimal Cost(TokenParts tokens, TokenRates? rates) => rates is null
        ? 0m
        : (tokens.Uncached / 1_000_000m * rates.Input) +
          (tokens.Cached / 1_000_000m * rates.Cached) +
          (tokens.Output / 1_000_000m * rates.Output);

    private static string PickFallbackModel(IReadOnlyList<CountModel> models)
    {
        var sol = models.FirstOrDefault(model => model.Model.StartsWith("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase));
        if (sol is not null)
        {
            return sol.Model;
        }

        return models
                   .Where(model => !model.Model.StartsWith("codex-auto-review", StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(model => model.Turns)
                   .FirstOrDefault()?.Model
               ?? "gpt-5.6-sol";
    }

    private static TokenParts ReadTokenParts(JsonElement element)
    {
        var cached = ReadFirstDecimal(
            element,
            "cached_text_input_tokens",
            "cached_input_tokens",
            "input_cached_tokens");
        var uncached = ReadFirstDecimal(
            element,
            "uncached_text_input_tokens",
            "uncached_input_tokens",
            "input_uncached_tokens");
        var inputTotal = ReadFirstDecimal(element, "input_tokens", "text_input_tokens");
        if (uncached <= Epsilon && inputTotal > Epsilon)
        {
            uncached = Math.Max(0m, inputTotal - cached);
        }

        var output = ReadFirstDecimal(element, "text_output_tokens", "output_tokens");
        var explicitTotal = ReadFirstDecimal(element, "text_total_tokens", "total_tokens");
        return new TokenParts(
            uncached,
            cached,
            output,
            explicitTotal > Epsilon ? explicitTotal : uncached + cached + output);
    }

    private static bool TryReadDate(JsonElement element, out DateOnly date)
    {
        var value = ReadString(element, "date") ?? ReadString(element, "day");
        return DateOnly.TryParseExact(
            value?.Length >= 10 ? value[..10] : value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal ReadFirstDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadDecimal(element, name);
            if (value > Epsilon)
            {
                return value;
            }
        }
        return 0m;
    }

    private static decimal ReadDecimal(JsonElement element, string name, decimal fallback = 0m)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(
                value.GetString(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }
        return fallback;
    }

    private static bool HasAnyProperty(JsonElement element, params string[] names) =>
        names.Any(name => element.TryGetProperty(name, out _));

    private sealed record TokenParts(
        decimal Uncached = 0m,
        decimal Cached = 0m,
        decimal Output = 0m,
        decimal Total = 0m)
    {
        internal TokenParts Add(TokenParts other) => new(
            Uncached + other.Uncached,
            Cached + other.Cached,
            Output + other.Output,
            Total + other.Total);

        internal TokenParts SubtractFloor(TokenParts other)
        {
            var uncached = Math.Max(0m, Uncached - other.Uncached);
            var cached = Math.Max(0m, Cached - other.Cached);
            var output = Math.Max(0m, Output - other.Output);
            return new TokenParts(uncached, cached, output, uncached + cached + output);
        }
    }

    private sealed record CountModel(
        string Model,
        string Speed,
        TokenParts Tokens,
        bool HasTokenDetails,
        decimal Turns,
        decimal Credits);

    private sealed record CountRow(
        DateOnly Date,
        TokenParts Tokens,
        decimal ReportedCredits,
        decimal Turns,
        IReadOnlyList<CountModel> Models);

    private sealed record BreakdownModel(string Model, string Speed, decimal Value);

    private sealed record BreakdownRow(DateOnly Date, IReadOnlyList<BreakdownModel> Models);

    private sealed record ModelAllocation(
        string Model,
        string Label,
        string Speed,
        TokenParts Tokens,
        decimal CodexCredits,
        decimal ApiStandardUsd,
        decimal ApiAtUseUsd,
        decimal ApiMatchedSpeedUsd,
        bool IsExact,
        ModelRates Rates);

    private sealed record TokenRates(decimal Input, decimal Cached, decimal Output);

    private sealed record ModelRates(
        string Label,
        TokenRates? Codex,
        TokenRates? Api,
        TokenRates? ApiAtUse,
        TokenRates? ApiFast,
        decimal CodexFastMultiplier,
        decimal ApiFastMultiplier,
        decimal CurrentToBilledRatio)
    {
        internal bool IsPriced => Codex is not null && Api is not null;
    }

    private static class Pricing
    {
        internal static ModelRates Resolve(string rawModel, DateOnly date)
        {
            var model = rawModel.Trim();
            var isHistorical = date < new DateOnly(2026, 7, 30);
            if (model.StartsWith("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))
            {
                return Rates("GPT-5.6 Sol", 125m, 12.5m, 750m, 5m, 0.5m, 30m, 10m, 1m, 60m, 2.5m, 2m);
            }
            if (model.StartsWith("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase))
            {
                return Rates(
                    "GPT-5.6 Terra",
                    62.5m, 6.25m, 375m,
                    2m, 0.2m, 12m,
                    4m, 0.4m, 24m,
                    2.5m, 2m,
                    isHistorical ? new TokenRates(2.5m, 0.25m, 15m) : null,
                    isHistorical ? 0.8m : 1m);
            }
            if (model.StartsWith("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
            {
                return Rates(
                    "GPT-5.6 Luna",
                    25m, 2.5m, 150m,
                    0.2m, 0.02m, 1.2m,
                    0.4m, 0.04m, 2.4m,
                    2.5m, 2m,
                    isHistorical ? new TokenRates(1m, 0.1m, 6m) : null,
                    isHistorical ? 0.2m : 1m);
            }
            if (model.StartsWith("gpt-5.5", StringComparison.OrdinalIgnoreCase) &&
                !model.StartsWith("gpt-5.5-cyber", StringComparison.OrdinalIgnoreCase))
            {
                return Rates("GPT-5.5", 125m, 12.5m, 750m, 5m, 0.5m, 30m, 12.5m, 1.25m, 75m, 2.5m, 2.5m);
            }
            if (model.StartsWith("gpt-5.4-mini", StringComparison.OrdinalIgnoreCase))
            {
                return Rates("GPT-5.4 Mini", 18.75m, 1.875m, 113m, 0.75m, 0.075m, 4.5m, 1.5m, 0.15m, 9m, 1m, 2m);
            }
            if (model.StartsWith("gpt-5.4-nano", StringComparison.OrdinalIgnoreCase))
            {
                return Rates("GPT-5.4 Nano", 5m, 0.5m, 31.25m, 0.2m, 0.02m, 1.25m, null, null, null, 1m, 1m);
            }
            if (model.StartsWith("gpt-5.4", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("codex-auto-review", StringComparison.OrdinalIgnoreCase))
            {
                return Rates(
                    model.StartsWith("codex-auto-review", StringComparison.OrdinalIgnoreCase)
                        ? "Code Review（GPT-5.4）"
                        : "GPT-5.4",
                    62.5m, 6.25m, 375m, 2.5m, 0.25m, 15m, 5m, 0.5m, 30m, 2m, 2m);
            }
            if (model.StartsWith("gpt-5.3-codex", StringComparison.OrdinalIgnoreCase))
            {
                return Rates("GPT-5.3 Codex", 43.75m, 4.375m, 350m, 1.75m, 0.175m, 14m, null, null, null, 1m, 1m);
            }
            if (model.StartsWith("gpt-image-2", StringComparison.OrdinalIgnoreCase))
            {
                return Rates("GPT-Image-2（仅文本 Token）", 125m, 31.25m, 250m, 5m, 1.25m, 0m, null, null, null, 1m, 1m);
            }
            return new ModelRates(model, null, null, null, null, 1m, 1m, 1m);
        }

        private static ModelRates Rates(
            string label,
            decimal codexInput,
            decimal codexCached,
            decimal codexOutput,
            decimal apiInput,
            decimal apiCached,
            decimal apiOutput,
            decimal? fastInput,
            decimal? fastCached,
            decimal? fastOutput,
            decimal codexFastMultiplier,
            decimal apiFastMultiplier,
            TokenRates? historicalApi = null,
            decimal currentToBilledRatio = 1m) =>
            new(
                label,
                new TokenRates(codexInput, codexCached, codexOutput),
                new TokenRates(apiInput, apiCached, apiOutput),
                historicalApi ?? new TokenRates(apiInput, apiCached, apiOutput),
                fastInput is decimal input && fastCached is decimal cached && fastOutput is decimal output
                    ? new TokenRates(input, cached, output)
                    : null,
                codexFastMultiplier,
                apiFastMultiplier,
                currentToBilledRatio);
    }

    private sealed class AnalyticsHttpException(HttpStatusCode statusCode, string message) : Exception(message)
    {
        internal HttpStatusCode StatusCode { get; } = statusCode;
    }
}
