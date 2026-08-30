using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

internal static class AuthFileParser
{
    internal static AuthCredentials Parse(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("认证文件的根节点不是 JSON 对象");
        }

        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("认证文件缺少 tokens");
        }

        var accessToken = GetPropertyString(tokens, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidDataException("认证文件缺少 access_token");
        }

        var idToken = GetPropertyString(tokens, "id_token");
        using var accessPayload = ParseJwtPayload(accessToken);
        using var idPayload = string.IsNullOrWhiteSpace(idToken) ? null : ParseJwtPayload(idToken);

        var accountId = FirstNonEmpty(
            GetPropertyString(tokens, "account_id"),
            FindString(accessPayload.RootElement, "chatgpt_account_id", "account_id"));
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new InvalidDataException("认证文件缺少账号 ID");
        }

        var name = FirstNonEmpty(
            idPayload is null ? null : FindString(idPayload.RootElement, "name"),
            FindString(accessPayload.RootElement, "name"),
            "未命名账号");
        var email = FirstNonEmpty(
            idPayload is null ? null : FindString(idPayload.RootElement, "email"),
            FindString(accessPayload.RootElement, "email"),
            "未提供邮箱");
        var plan = FirstNonEmpty(
            FindString(accessPayload.RootElement, "chatgpt_plan_type", "plan_type"),
            idPayload is null ? null : FindString(idPayload.RootElement, "chatgpt_plan_type", "plan_type"),
            "未知套餐");

        var normalizedPlan = NormalizePlan(plan);
        var workspaceName = normalizedPlan is "Team" or "Business" or "Enterprise"
            ? FirstNonEmptyOrNull(
                idPayload is null ? null : FindWorkspaceName(idPayload.RootElement, accountId),
                FindWorkspaceName(accessPayload.RootElement, accountId))
            : null;
        var expiresAt = ReadExpiry(accessPayload.RootElement);
        return new AuthCredentials(accessToken, accountId, name, email, normalizedPlan, expiresAt, workspaceName);
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static JsonDocument ParseJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidDataException("令牌不是有效的 JWT");
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            return JsonDocument.Parse(Convert.FromBase64String(payload));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidDataException("JWT 载荷无法解析", exception);
        }
    }

    private static string? FindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }

                var nested = FindString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadExpiry(JsonElement payload)
    {
        if (!payload.TryGetProperty("exp", out var expiry))
        {
            return null;
        }

        if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var seconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? FindWorkspaceName(JsonElement element, string accountId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("organizations", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    var organizations = property.Value.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Object)
                        .Select(item => new
                        {
                            Id = GetPropertyString(item, "id"),
                            Title = GetPropertyString(item, "title")?.Trim(),
                        })
                        .Where(item => !string.IsNullOrWhiteSpace(item.Title) &&
                                       !item.Title.Equals("personal", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var matched = organizations.FirstOrDefault(item =>
                        string.Equals(item.Id, accountId, StringComparison.OrdinalIgnoreCase));
                    if (matched is not null)
                    {
                        return matched.Title;
                    }

                    var uniqueTitles = organizations
                        .Select(item => item.Title!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (uniqueTitles.Length == 1)
                    {
                        return uniqueTitles[0];
                    }
                }

                var nested = FindWorkspaceName(property.Value, accountId);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindWorkspaceName(item, accountId);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? GetPropertyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? FirstNonEmptyOrNull(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizePlan(string plan)
    {
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
