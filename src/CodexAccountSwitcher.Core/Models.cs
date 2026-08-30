namespace CodexAccountSwitcher.Core;

public enum QuotaStatus
{
    NotLoaded,
    Loading,
    Available,
    Unauthorized,
    Forbidden,
    Timeout,
    Error,
}

public sealed record AccountSummary(
    string Slot,
    bool IsCurrent,
    string DisplayName,
    string Email,
    string Plan,
    string AccountId,
    string ContentHash,
    DateTimeOffset? TokenExpiresAt,
    bool IsUsable,
    string? ErrorMessage,
    string? WorkspaceName = null);

public enum WorkspaceNameStatus
{
    NotRequired,
    Available,
    Unauthorized,
    Forbidden,
    Timeout,
    Error,
}

public sealed record WorkspaceNameInfo(
    WorkspaceNameStatus Status,
    string? Name,
    string? ErrorMessage)
{
    public static WorkspaceNameInfo NotRequired() =>
        new(WorkspaceNameStatus.NotRequired, null, null);

    public static WorkspaceNameInfo Failure(WorkspaceNameStatus status, string message) =>
        new(status, null, message);
}

public sealed record UsageWindow(
    double UsedPercent,
    int? LimitWindowSeconds,
    DateTimeOffset? ResetAt)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

public sealed record QuotaInfo(
    QuotaStatus Status,
    string? Plan,
    UsageWindow? Primary,
    UsageWindow? Secondary,
    decimal? CreditBalance,
    DateTimeOffset RetrievedAt,
    string? ErrorMessage)
{
    public static QuotaInfo Failure(QuotaStatus status, string message) =>
        new(status, null, null, null, null, DateTimeOffset.Now, message);
}

public sealed record SwitchResult(string PreviousSlot, string CurrentSlot, string ActiveHash);

public sealed record LoginResult(
    string DisplayName,
    string Email,
    string Plan,
    string ArchivedPreviousSlot,
    bool ReusedExistingSlot,
    bool RefreshedCurrentAccount);

public sealed record ChatGptRestartResult(
    bool Success,
    int ClosedProcessCount,
    int ForcedProcessCount,
    string? ErrorMessage);

public sealed record ChatGptStopResult(
    bool Success,
    int ClosedProcessCount,
    int ForcedProcessCount,
    string? ErrorMessage);

public sealed record ChatGptStartResult(bool Success, string? ErrorMessage);

public enum TokenCostStatus
{
    Available,
    Unauthorized,
    Forbidden,
    Timeout,
    Error,
}

public sealed record TokenCostModelRow(
    string Model,
    string Label,
    string Speed,
    decimal UncachedInputTokens,
    decimal CachedInputTokens,
    decimal OutputTokens,
    decimal TotalTokens,
    decimal CodexCredits,
    decimal ApiStandardUsd,
    decimal ApiAtUseUsd,
    decimal ApiMatchedSpeedUsd,
    bool IsExact,
    bool IsPriced);

public sealed record TokenCostAnalysis(
    TokenCostStatus Status,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal UncachedInputTokens,
    decimal CachedInputTokens,
    decimal OutputTokens,
    decimal TotalTokens,
    decimal ComputedCredits,
    decimal ReportedCredits,
    decimal ApiStandardUsd,
    decimal ApiAtUseUsd,
    decimal ApiMatchedSpeedUsd,
    decimal FastCredits,
    decimal CacheHitRate,
    decimal ExactModelTokenCoverage,
    decimal PricedTokenCoverage,
    decimal Turns,
    int ActiveDays,
    string DataSource,
    string AnalyzerVersion,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<TokenCostModelRow> Models,
    string? Warning,
    string? ErrorMessage)
{
    public static TokenCostAnalysis Failure(
        TokenCostStatus status,
        DateOnly startDate,
        DateOnly endDate,
        string message) =>
        new(
            status,
            startDate,
            endDate,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "未读取",
            "4.4.0-native",
            DateTimeOffset.Now,
            [],
            null,
            message);
}

internal sealed record AuthCredentials(
    string AccessToken,
    string AccountId,
    string Name,
    string Email,
    string Plan,
    DateTimeOffset? ExpiresAt,
    string? WorkspaceName);
