using System.Text.Json;
using System.Security.Cryptography;

namespace CodexAccountSwitcher.Core;

public sealed class AuthSwitchService
{
    private const string LockFileName = ".account-switcher.lock";
    private const string JournalFileName = ".account-switcher-transaction.json";
    private readonly string _authDirectory;
    private readonly AuthAccountCatalog _catalog;

    public AuthSwitchService(string authDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authDirectory);
        _authDirectory = Path.GetFullPath(authDirectory);
        _catalog = new AuthAccountCatalog(_authDirectory);
    }

    public SwitchResult Switch(string targetSlot, string expectedTargetHash)
    {
        return SwitchCore(targetSlot, expectedTargetHash, null, recoverOnFailure: true);
    }

    public void DeleteSavedAccount(string targetSlot, string expectedTargetHash)
    {
        if (targetSlot.Equals(AuthAccountCatalog.CurrentSlot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("当前账号不能删除，请先切换到其他账号");
        }

        if (string.IsNullOrWhiteSpace(expectedTargetHash))
        {
            throw new ArgumentException("缺少目标文件哈希", nameof(expectedTargetHash));
        }

        AuthAccountCatalog.EnsureSafeDirectory(_authDirectory);
        using var transactionLock = AcquireLock();
        RecoverCore();

        var targetPath = _catalog.ResolveSlot(targetSlot);
        AuthAccountCatalog.EnsureSafeFile(targetPath);

        using var targetLock = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var actualHash = Convert.ToHexString(SHA256.HashData(targetLock));
        if (!actualHash.Equals(expectedTargetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("账号文件在确认后发生了变化，请刷新列表后重试");
        }

        File.Delete(targetPath);
        if (File.Exists(targetPath))
        {
            throw new IOException("账号文件删除失败");
        }
    }

    public bool RecoverPending()
    {
        AuthAccountCatalog.EnsureSafeDirectory(_authDirectory);
        using var transactionLock = AcquireLock();
        return RecoverCore();
    }

    internal SwitchResult SwitchForTest(
        string targetSlot,
        string expectedTargetHash,
        SwitchFaultPoint faultPoint,
        bool recoverOnFailure)
    {
        return SwitchCore(targetSlot, expectedTargetHash, faultPoint, recoverOnFailure);
    }

    private SwitchResult SwitchCore(
        string targetSlot,
        string expectedTargetHash,
        SwitchFaultPoint? faultPoint,
        bool recoverOnFailure)
    {
        if (targetSlot.Equals(AuthAccountCatalog.CurrentSlot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("该账号已经是当前账号");
        }

        if (string.IsNullOrWhiteSpace(expectedTargetHash))
        {
            throw new ArgumentException("缺少目标文件哈希", nameof(expectedTargetHash));
        }

        AuthAccountCatalog.EnsureSafeDirectory(_authDirectory);
        using var transactionLock = AcquireLock();
        RecoverCore();

        var activePath = _catalog.ResolveSlot(AuthAccountCatalog.CurrentSlot);
        var targetPath = _catalog.ResolveSlot(targetSlot);
        AuthAccountCatalog.EnsureSafeFile(activePath);
        AuthAccountCatalog.EnsureSafeFile(targetPath);

        var activeCredentials = AuthFileParser.Parse(activePath);
        var targetCredentials = AuthFileParser.Parse(targetPath);
        if (activeCredentials.AccountId.Equals(targetCredentials.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("目标槽位与当前槽位是同一账号，已拒绝切换");
        }

        var activeHash = AuthFileParser.ComputeSha256(activePath);
        var targetHash = AuthFileParser.ComputeSha256(targetPath);
        if (!targetHash.Equals(expectedTargetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目标账号文件在确认后发生了变化，请刷新列表后重试");
        }

        var tempName = $".account-switcher-{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Combine(_authDirectory, tempName);
        var journal = new TransactionJournal(
            1,
            TransactionStage.Prepared,
            AuthAccountCatalog.CurrentSlot,
            targetSlot,
            tempName,
            activeHash,
            targetHash,
            DateTimeOffset.UtcNow);

        try
        {
            WriteJournal(journal);
            ThrowIfRequested(faultPoint, SwitchFaultPoint.AfterPrepared);

            File.Move(activePath, tempPath);
            ThrowIfRequested(faultPoint, SwitchFaultPoint.AfterActiveMoved);
            journal = journal with { Stage = TransactionStage.ActiveMoved };
            WriteJournal(journal);

            File.Move(targetPath, activePath);
            ThrowIfRequested(faultPoint, SwitchFaultPoint.AfterTargetPromoted);
            journal = journal with { Stage = TransactionStage.TargetPromoted };
            WriteJournal(journal);

            File.Move(tempPath, targetPath);
            ThrowIfRequested(faultPoint, SwitchFaultPoint.AfterSwapCompleted);
            journal = journal with { Stage = TransactionStage.Completed };
            WriteJournal(journal);

            ValidateCompleted(journal);
            DeleteJournal();
            return new SwitchResult(targetSlot, AuthAccountCatalog.CurrentSlot, targetHash);
        }
        catch
        {
            if (recoverOnFailure)
            {
                try
                {
                    RecoverCore();
                }
                catch
                {
                    // Keep the original error. The journal remains for deterministic recovery on next start.
                }
            }

            throw;
        }
    }

    private bool RecoverCore()
    {
        var journalPath = Path.Combine(_authDirectory, JournalFileName);
        if (!File.Exists(journalPath))
        {
            var abandonedNextPath = journalPath + ".next";
            if (File.Exists(abandonedNextPath))
            {
                AuthAccountCatalog.EnsureSafeFile(abandonedNextPath);
                File.Delete(abandonedNextPath);
            }

            return false;
        }

        AuthAccountCatalog.EnsureSafeFile(journalPath);
        var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(journalPath), JsonOptions)
            ?? throw new InvalidDataException("切换事务日志为空");
        ValidateJournal(journal);

        var activePath = _catalog.ResolveSlot(journal.ActiveSlot);
        var targetPath = _catalog.ResolveSlot(journal.TargetSlot);
        var tempPath = ResolveTransactionTemp(journal.TempName);
        var activeHash = HashIfExists(activePath);
        var targetHash = HashIfExists(targetPath);
        var tempHash = HashIfExists(tempPath);

        if (HashEquals(activeHash, journal.TargetHash) && HashEquals(targetHash, journal.ActiveHash))
        {
            if (tempHash is not null)
            {
                if (!HashEquals(tempHash, journal.ActiveHash))
                {
                    throw new InvalidDataException("事务临时文件内容不匹配");
                }

                File.Delete(tempPath);
            }

            DeleteJournal();
            return true;
        }

        if (HashEquals(activeHash, journal.TargetHash) && targetHash is null && HashEquals(tempHash, journal.ActiveHash))
        {
            File.Move(tempPath, targetPath);
            ValidateCompleted(journal);
            DeleteJournal();
            return true;
        }

        if (activeHash is null && HashEquals(targetHash, journal.TargetHash) && HashEquals(tempHash, journal.ActiveHash))
        {
            File.Move(tempPath, activePath);
            ValidateRolledBack(journal);
            DeleteJournal();
            return true;
        }

        if (HashEquals(activeHash, journal.ActiveHash) && HashEquals(targetHash, journal.TargetHash))
        {
            if (tempHash is not null)
            {
                if (!HashEquals(tempHash, journal.ActiveHash))
                {
                    throw new InvalidDataException("事务临时文件内容不匹配");
                }

                File.Delete(tempPath);
            }

            DeleteJournal();
            return true;
        }

        throw new InvalidDataException("无法安全判定中断事务状态；认证文件未被继续修改");
    }

    private TransactionLock AcquireLock()
    {
        var lockPath = Path.Combine(_authDirectory, LockFileName);
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("切换锁不能是符号链接或重解析点");
        }

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new TransactionLock(lockPath, stream);
        }
        catch (IOException exception)
        {
            throw new IOException("另一个账号操作正在进行，请稍后重试", exception);
        }
    }

    private void WriteJournal(TransactionJournal journal)
    {
        var journalPath = Path.Combine(_authDirectory, JournalFileName);
        var nextPath = journalPath + ".next";
        RejectReparsePointIfPresent(journalPath);
        RejectReparsePointIfPresent(nextPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);

        using (var stream = new FileStream(nextPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(nextPath, journalPath, overwrite: true);
    }

    private void DeleteJournal()
    {
        var journalPath = Path.Combine(_authDirectory, JournalFileName);
        if (File.Exists(journalPath))
        {
            AuthAccountCatalog.EnsureSafeFile(journalPath);
            File.Delete(journalPath);
        }

        var nextPath = journalPath + ".next";
        if (File.Exists(nextPath))
        {
            AuthAccountCatalog.EnsureSafeFile(nextPath);
            File.Delete(nextPath);
        }
    }

    private static void RejectReparsePointIfPresent(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("事务文件不能是符号链接或重解析点");
        }
    }

    private string ResolveTransactionTemp(string tempName)
    {
        if (!tempName.StartsWith(".account-switcher-", StringComparison.Ordinal) ||
            !tempName.EndsWith(".tmp", StringComparison.Ordinal) ||
            Path.GetFileName(tempName) != tempName)
        {
            throw new InvalidDataException("事务临时文件名无效");
        }

        return Path.Combine(_authDirectory, tempName);
    }

    private static void ValidateJournal(TransactionJournal journal)
    {
        if (journal.Version != 1 ||
            journal.ActiveSlot != AuthAccountCatalog.CurrentSlot ||
            journal.TargetSlot == AuthAccountCatalog.CurrentSlot ||
            !AuthAccountCatalog.IsValidSlot(journal.TargetSlot) ||
            string.IsNullOrWhiteSpace(journal.ActiveHash) ||
            string.IsNullOrWhiteSpace(journal.TargetHash))
        {
            throw new InvalidDataException("切换事务日志格式无效");
        }
    }

    private void ValidateCompleted(TransactionJournal journal)
    {
        var activeHash = AuthFileParser.ComputeSha256(_catalog.ResolveSlot(journal.ActiveSlot));
        var targetHash = AuthFileParser.ComputeSha256(_catalog.ResolveSlot(journal.TargetSlot));
        if (!HashEquals(activeHash, journal.TargetHash) || !HashEquals(targetHash, journal.ActiveHash))
        {
            throw new IOException("交换后的认证文件校验失败");
        }
    }

    private void ValidateRolledBack(TransactionJournal journal)
    {
        var activeHash = AuthFileParser.ComputeSha256(_catalog.ResolveSlot(journal.ActiveSlot));
        var targetHash = AuthFileParser.ComputeSha256(_catalog.ResolveSlot(journal.TargetSlot));
        if (!HashEquals(activeHash, journal.ActiveHash) || !HashEquals(targetHash, journal.TargetHash))
        {
            throw new IOException("回滚后的认证文件校验失败");
        }
    }

    private static string? HashIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        AuthAccountCatalog.EnsureSafeFile(path);
        return AuthFileParser.ComputeSha256(path);
    }

    private static bool HashEquals(string? left, string right) =>
        left is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfRequested(SwitchFaultPoint? requested, SwitchFaultPoint current)
    {
        if (requested == current)
        {
            throw new SimulatedCrashException(current);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class TransactionLock(string path, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A competing instance may have acquired the same lock file after release.
            }
            catch (UnauthorizedAccessException)
            {
                // The empty lock file is harmless and can be reused on the next run.
            }
        }
    }
}

internal enum TransactionStage
{
    Prepared,
    ActiveMoved,
    TargetPromoted,
    Completed,
}

internal enum SwitchFaultPoint
{
    AfterPrepared,
    AfterActiveMoved,
    AfterTargetPromoted,
    AfterSwapCompleted,
}

internal sealed record TransactionJournal(
    int Version,
    TransactionStage Stage,
    string ActiveSlot,
    string TargetSlot,
    string TempName,
    string ActiveHash,
    string TargetHash,
    DateTimeOffset CreatedAt);

internal sealed class SimulatedCrashException(SwitchFaultPoint point)
    : Exception($"Simulated crash at {point}");
