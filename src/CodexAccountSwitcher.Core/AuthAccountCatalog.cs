using System.Text.RegularExpressions;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

public sealed partial class AuthAccountCatalog
{
    public const string CurrentSlot = "auth.json";
    private readonly string _authDirectory;

    public AuthAccountCatalog(string authDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authDirectory);
        _authDirectory = Path.GetFullPath(authDirectory);
    }

    public IReadOnlyList<AccountSummary> Load()
    {
        EnsureSafeDirectory(_authDirectory);

        var candidates = Directory.EnumerateFiles(_authDirectory, "auth.json*", SearchOption.TopDirectoryOnly)
            .Where(path => IsValidSlot(Path.GetFileName(path)))
            .OrderBy(path => SlotOrder(Path.GetFileName(path)))
            .ToArray();

        var accounts = new List<AccountSummary>(candidates.Length);
        var knownAccountIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            var slot = Path.GetFileName(path);
            try
            {
                EnsureSafeFile(path);
                var credentials = AuthFileParser.Parse(path);
                var duplicateOf = knownAccountIds.GetValueOrDefault(credentials.AccountId);
                if (duplicateOf is null)
                {
                    knownAccountIds.Add(credentials.AccountId, slot);
                }

                accounts.Add(new AccountSummary(
                    slot,
                    slot.Equals(CurrentSlot, StringComparison.Ordinal),
                    credentials.Name,
                    credentials.Email,
                    credentials.Plan,
                    credentials.AccountId,
                    AuthFileParser.ComputeSha256(path),
                    credentials.ExpiresAt,
                    duplicateOf is null,
                    duplicateOf is null ? null : "检测到重复账号",
                    credentials.WorkspaceName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                accounts.Add(new AccountSummary(
                    slot,
                    slot.Equals(CurrentSlot, StringComparison.Ordinal),
                    "无法读取的账号",
                    "—",
                    "—",
                    string.Empty,
                    TryComputeHash(path),
                    null,
                    false,
                    SafeError(exception)));
            }
        }

        return accounts;
    }

    public string ResolveSlot(string slot)
    {
        if (!IsValidSlot(slot))
        {
            throw new ArgumentException("账号槽位名称无效", nameof(slot));
        }

        var path = Path.GetFullPath(Path.Combine(_authDirectory, slot));
        var expectedParent = _authDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("账号槽位越出认证目录");
        }

        return path;
    }

    internal static bool IsValidSlot(string? slot) =>
        slot is not null && SlotNameRegex().IsMatch(slot);

    internal static void EnsureSafeDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"认证目录不存在：{directory}");
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("认证目录不能是符号链接或重解析点");
        }
    }

    internal static void EnsureSafeFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("账号文件不存在", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("账号文件不能是符号链接或重解析点");
        }
    }

    private static int SlotOrder(string slot)
    {
        if (slot.Equals(CurrentSlot, StringComparison.Ordinal))
        {
            return -1;
        }

        return int.TryParse(slot[CurrentSlot.Length..], out var number) ? number : int.MaxValue;
    }

    private static string TryComputeHash(string path)
    {
        try
        {
            return AuthFileParser.ComputeSha256(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "没有权限读取认证文件",
        JsonException => "认证文件不是有效 JSON",
        InvalidDataException => exception.Message,
        _ => "认证文件读取失败",
    };

    [GeneratedRegex("^auth\\.json(?:0|[1-9][0-9]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlotNameRegex();
}
