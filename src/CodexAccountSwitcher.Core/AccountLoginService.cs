using System.Text.Json;

namespace CodexAccountSwitcher.Core;

public sealed class AccountLoginService
{
    private const string LockFileName = ".account-switcher.lock";
    private const string JournalFileName = ".account-login-transaction.json";
    private readonly string _authDirectory;
    private readonly AuthAccountCatalog _catalog;

    public AccountLoginService(string authDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authDirectory);
        _authDirectory = Path.GetFullPath(authDirectory);
        _catalog = new AuthAccountCatalog(_authDirectory);
    }

    public Task<LoginResult> LoginNewAccountAsync(
        Func<string, CancellationToken, Task<int>> loginRunner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginRunner);
        return LoginCoreAsync(loginRunner, null, recoverOnFailure: true, cancellationToken);
    }

    public bool RecoverPending()
    {
        AuthAccountCatalog.EnsureSafeDirectory(_authDirectory);
        using var transactionLock = AcquireLock();
        return RecoverCore();
    }

    internal Task<LoginResult> LoginForTestAsync(
        Func<string, CancellationToken, Task<int>> loginRunner,
        LoginFaultPoint faultPoint,
        bool recoverOnFailure)
    {
        return LoginCoreAsync(loginRunner, faultPoint, recoverOnFailure, CancellationToken.None);
    }

    private async Task<LoginResult> LoginCoreAsync(
        Func<string, CancellationToken, Task<int>> loginRunner,
        LoginFaultPoint? faultPoint,
        bool recoverOnFailure,
        CancellationToken cancellationToken)
    {
        AuthAccountCatalog.EnsureSafeDirectory(_authDirectory);
        using var transactionLock = AcquireLock();
        RecoverCore();
        ValidateExistingAccounts();

        var transactionId = Guid.NewGuid().ToString("N");
        var tempDirectoryName = $".account-login-{transactionId}";
        var tempDirectory = ResolveLoginDirectory(tempDirectoryName);
        Directory.CreateDirectory(tempDirectory);

        var journal = new LoginJournal(
            1,
            LoginStage.AwaitingLogin,
            tempDirectoryName,
            $".account-login-old-{transactionId}.tmp",
            $".account-login-displaced-{transactionId}.tmp",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
        WriteJournal(journal);

        LoginJournal? readyJournal = null;
        try
        {
            var exitCode = await loginRunner(tempDirectory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var loginAuthPath = Path.Combine(tempDirectory, AuthAccountCatalog.CurrentSlot);
            if (!File.Exists(loginAuthPath))
            {
                throw new InvalidOperationException(
                    exitCode == 0 ? "登录流程没有生成认证文件" : "登录已取消或未完成");
            }

            AuthAccountCatalog.EnsureSafeFile(loginAuthPath);
            AuthCredentials newCredentials;
            try
            {
                newCredentials = AuthFileParser.Parse(loginAuthPath);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("新账号认证文件不是有效 JSON", exception);
            }
            readyJournal = PrepareImport(journal, newCredentials);
            CompleteImport(readyJournal, faultPoint);

            return new LoginResult(
                newCredentials.Name,
                newCredentials.Email,
                newCredentials.Plan,
                readyJournal.TargetSlot ?? AuthAccountCatalog.CurrentSlot,
                readyJournal.TargetOriginalHash is not null,
                readyJournal.TargetSlot is null);
        }
        catch
        {
            if (readyJournal is null)
            {
                CleanupAwaitingLogin(journal);
            }
            else if (recoverOnFailure)
            {
                try
                {
                    RecoverCore();
                }
                catch
                {
                    // Leave the hash-only journal for deterministic recovery on next start.
                }
            }

            throw;
        }
    }

    private LoginJournal PrepareImport(LoginJournal journal, AuthCredentials newCredentials)
    {
        var activePath = _catalog.ResolveSlot(AuthAccountCatalog.CurrentSlot);
        AuthAccountCatalog.EnsureSafeFile(activePath);
        var activeCredentials = AuthFileParser.Parse(activePath);
        var activeHash = AuthFileParser.ComputeSha256(activePath);
        var newAuthPath = Path.Combine(ResolveLoginDirectory(journal.TempDirectoryName), AuthAccountCatalog.CurrentSlot);
        var newHash = AuthFileParser.ComputeSha256(newAuthPath);

        string? targetSlot = null;
        string? targetOriginalHash = null;
        if (!activeCredentials.AccountId.Equals(newCredentials.AccountId, StringComparison.Ordinal))
        {
            var accounts = _catalog.Load();
            var matchingSlots = accounts
                .Where(account => !account.IsCurrent && account.AccountId.Equals(newCredentials.AccountId, StringComparison.Ordinal))
                .Select(account => account.Slot)
                .ToArray();
            if (matchingSlots.Length > 1)
            {
                throw new InvalidOperationException("该登录账号已经存在于多个槽位，已拒绝自动导入");
            }

            targetSlot = matchingSlots.SingleOrDefault() ?? FindNextAvailableSlot(accounts.Select(account => account.Slot));
            var targetPath = _catalog.ResolveSlot(targetSlot);
            if (File.Exists(targetPath))
            {
                AuthAccountCatalog.EnsureSafeFile(targetPath);
                targetOriginalHash = AuthFileParser.ComputeSha256(targetPath);
            }
        }

        var ready = journal with
        {
            Stage = LoginStage.ReadyToImport,
            TargetSlot = targetSlot,
            ActiveHash = activeHash,
            NewHash = newHash,
            TargetOriginalHash = targetOriginalHash,
        };
        WriteJournal(ready);
        return ready;
    }

    private void CompleteImport(LoginJournal journal, LoginFaultPoint? faultPoint)
    {
        ValidateReadyJournal(journal);
        var activePath = _catalog.ResolveSlot(AuthAccountCatalog.CurrentSlot);
        var loginDirectory = ResolveLoginDirectory(journal.TempDirectoryName);
        var newAuthPath = Path.Combine(loginDirectory, AuthAccountCatalog.CurrentSlot);
        var oldTempPath = ResolveTransactionTemp(journal.OldTempName, ".account-login-old-");
        var displacedPath = ResolveTransactionTemp(journal.DisplacedName, ".account-login-displaced-");

        var activeHash = HashIfExists(activePath);
        var newHash = HashIfExists(newAuthPath);
        var oldTempHash = HashIfExists(oldTempPath);

        if (HashEquals(journal.ActiveHash, journal.NewHash))
        {
            if (!HashEquals(activeHash, journal.ActiveHash))
            {
                throw new InvalidDataException("当前账号在登录期间发生了无法识别的变化");
            }

            DeleteExpectedFile(newAuthPath, journal.NewHash);
            DeleteExpectedFile(oldTempPath, journal.ActiveHash);
            CleanupCompletedLogin(journal);
            return;
        }

        if (HashEquals(activeHash, journal.ActiveHash))
        {
            if (oldTempHash is not null)
            {
                throw new InvalidDataException("登录事务包含重复的旧账号临时文件");
            }

            File.Move(activePath, oldTempPath);
            journal = journal with { Stage = LoginStage.ActiveMoved };
            WriteJournal(journal);
            ThrowIfRequested(faultPoint, LoginFaultPoint.AfterActiveMoved);
            activeHash = null;
            oldTempHash = journal.ActiveHash;
        }

        if (activeHash is null && HashEquals(newHash, journal.NewHash))
        {
            File.Move(newAuthPath, activePath);
            journal = journal with { Stage = LoginStage.NewPromoted };
            WriteJournal(journal);
            ThrowIfRequested(faultPoint, LoginFaultPoint.AfterNewPromoted);
            activeHash = journal.NewHash;
            newHash = null;
        }

        if (!HashEquals(activeHash, journal.NewHash))
        {
            throw new InvalidDataException("无法确认新登录账号已写入 auth.json");
        }

        if (newHash is not null)
        {
            if (!HashEquals(newHash, journal.NewHash))
            {
                throw new InvalidDataException("登录临时认证文件内容不匹配");
            }

            File.Delete(newAuthPath);
        }

        if (journal.TargetSlot is null)
        {
            DeleteExpectedFile(oldTempPath, journal.ActiveHash);
            CleanupCompletedLogin(journal);
            return;
        }

        var targetPath = _catalog.ResolveSlot(journal.TargetSlot);
        var targetHash = HashIfExists(targetPath);
        var displacedHash = HashIfExists(displacedPath);
        oldTempHash = HashIfExists(oldTempPath);

        if (!HashEquals(targetHash, journal.ActiveHash))
        {
            if (journal.TargetOriginalHash is null)
            {
                if (targetHash is not null)
                {
                    throw new InvalidDataException("新账号槽位已被其他文件占用");
                }
            }
            else if (HashEquals(targetHash, journal.TargetOriginalHash))
            {
                if (displacedHash is not null)
                {
                    throw new InvalidDataException("登录事务包含重复的被替换账号文件");
                }

                File.Move(targetPath, displacedPath);
                journal = journal with { Stage = LoginStage.TargetDisplaced };
                WriteJournal(journal);
                ThrowIfRequested(faultPoint, LoginFaultPoint.AfterTargetDisplaced);
                targetHash = null;
                displacedHash = journal.TargetOriginalHash;
            }
            else if (targetHash is not null)
            {
                throw new InvalidDataException("已有账号槽位在登录期间发生了变化");
            }

            if (targetHash is null)
            {
                if (!HashEquals(oldTempHash, journal.ActiveHash))
                {
                    throw new InvalidDataException("旧的当前账号临时文件缺失");
                }

                File.Move(oldTempPath, targetPath);
                journal = journal with { Stage = LoginStage.PreviousStored };
                WriteJournal(journal);
                ThrowIfRequested(faultPoint, LoginFaultPoint.AfterPreviousStored);
                targetHash = journal.ActiveHash;
                oldTempHash = null;
            }
        }

        if (!HashEquals(targetHash, journal.ActiveHash))
        {
            throw new IOException("旧的当前账号未能保存到目标槽位");
        }

        if (oldTempHash is not null)
        {
            DeleteExpectedFile(oldTempPath, journal.ActiveHash);
        }

        if (displacedHash is not null)
        {
            DeleteExpectedFile(displacedPath, journal.TargetOriginalHash!);
        }

        CleanupCompletedLogin(journal);
    }

    private bool RecoverCore()
    {
        var journalPath = Path.Combine(_authDirectory, JournalFileName);
        if (!File.Exists(journalPath))
        {
            DeleteAbandonedJournalNext(journalPath + ".next");
            return false;
        }

        AuthAccountCatalog.EnsureSafeFile(journalPath);
        var journal = JsonSerializer.Deserialize<LoginJournal>(File.ReadAllBytes(journalPath), JsonOptions)
            ?? throw new InvalidDataException("登录事务日志为空");
        ValidateJournal(journal);

        if (journal.Stage == LoginStage.AwaitingLogin)
        {
            var loginAuthPath = Path.Combine(ResolveLoginDirectory(journal.TempDirectoryName), AuthAccountCatalog.CurrentSlot);
            if (!File.Exists(loginAuthPath))
            {
                CleanupAwaitingLogin(journal);
                return true;
            }

            try
            {
                var newCredentials = AuthFileParser.Parse(loginAuthPath);
                journal = PrepareImport(journal, newCredentials);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                CleanupAwaitingLogin(journal);
                return true;
            }
        }

        CompleteImport(journal, null);
        return true;
    }

    private void ValidateExistingAccounts()
    {
        var accounts = _catalog.Load();
        var current = accounts.SingleOrDefault(account => account.IsCurrent)
            ?? throw new InvalidOperationException("未找到当前 auth.json");
        if (!current.IsUsable)
        {
            throw new InvalidOperationException(current.ErrorMessage ?? "当前账号文件不可用");
        }

        var invalid = accounts.FirstOrDefault(account => !account.IsUsable);
        if (invalid is not null)
        {
            throw new InvalidOperationException($"请先处理 {invalid.Slot}：{invalid.ErrorMessage}");
        }
    }

    private string FindNextAvailableSlot(IEnumerable<string> slots)
    {
        var occupied = slots.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < int.MaxValue; index++)
        {
            var slot = $"auth.json{index}";
            if (!occupied.Contains(slot) && !File.Exists(_catalog.ResolveSlot(slot)))
            {
                return slot;
            }
        }

        throw new IOException("没有可用的账号槽位");
    }

    private void CleanupAwaitingLogin(LoginJournal journal)
    {
        DeleteLoginDirectory(journal.TempDirectoryName);
        DeleteExpectedFile(ResolveTransactionTemp(journal.OldTempName, ".account-login-old-"), null);
        DeleteExpectedFile(ResolveTransactionTemp(journal.DisplacedName, ".account-login-displaced-"), null);
        DeleteJournal();
    }

    private void CleanupCompletedLogin(LoginJournal journal)
    {
        var activeHash = AuthFileParser.ComputeSha256(_catalog.ResolveSlot(AuthAccountCatalog.CurrentSlot));
        if (!HashEquals(activeHash, journal.NewHash!))
        {
            throw new IOException("新登录账号的最终哈希校验失败");
        }

        if (journal.TargetSlot is not null)
        {
            var targetHash = AuthFileParser.ComputeSha256(_catalog.ResolveSlot(journal.TargetSlot));
            if (!HashEquals(targetHash, journal.ActiveHash!))
            {
                throw new IOException("旧账号归档槽位的最终哈希校验失败");
            }
        }

        DeleteLoginDirectory(journal.TempDirectoryName);
        DeleteJournal();
    }

    private void DeleteLoginDirectory(string directoryName)
    {
        var path = ResolveLoginDirectory(directoryName);
        if (!Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("登录临时目录包含符号链接或重解析点");
        }

        Directory.Delete(path, recursive: true);
    }

    private string ResolveLoginDirectory(string directoryName)
    {
        const string prefix = ".account-login-";
        if (!directoryName.StartsWith(prefix, StringComparison.Ordinal) ||
            directoryName.Length != prefix.Length + 32 ||
            !Guid.TryParseExact(directoryName[prefix.Length..], "N", out _) ||
            Path.GetFileName(directoryName) != directoryName)
        {
            throw new InvalidDataException("登录临时目录名无效");
        }

        var path = Path.GetFullPath(Path.Combine(_authDirectory, directoryName));
        EnsureChildPath(path);
        return path;
    }

    private string ResolveTransactionTemp(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            fileName.Length != prefix.Length + 32 + ".tmp".Length ||
            !fileName.EndsWith(".tmp", StringComparison.Ordinal) ||
            !Guid.TryParseExact(fileName.Substring(prefix.Length, 32), "N", out _) ||
            Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidDataException("登录事务临时文件名无效");
        }

        var path = Path.GetFullPath(Path.Combine(_authDirectory, fileName));
        EnsureChildPath(path);
        return path;
    }

    private void EnsureChildPath(string path)
    {
        var expectedParent = _authDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("登录事务路径越出认证目录");
        }
    }

    private void WriteJournal(LoginJournal journal)
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
        foreach (var path in new[]
                 {
                     Path.Combine(_authDirectory, JournalFileName),
                     Path.Combine(_authDirectory, JournalFileName + ".next"),
                 })
        {
            if (File.Exists(path))
            {
                AuthAccountCatalog.EnsureSafeFile(path);
                File.Delete(path);
            }
        }
    }

    private static void DeleteAbandonedJournalNext(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        AuthAccountCatalog.EnsureSafeFile(path);
        File.Delete(path);
    }

    private static void DeleteExpectedFile(string path, string? expectedHash)
    {
        if (!File.Exists(path))
        {
            return;
        }

        AuthAccountCatalog.EnsureSafeFile(path);
        if (expectedHash is not null && !HashEquals(AuthFileParser.ComputeSha256(path), expectedHash))
        {
            throw new InvalidDataException("拒绝删除内容不匹配的登录事务文件");
        }

        File.Delete(path);
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

    private static bool HashEquals(string? left, string? right) =>
        left is not null && right is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePointIfPresent(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("登录事务文件不能是符号链接或重解析点");
        }
    }

    private TransactionLock AcquireLock()
    {
        var lockPath = Path.Combine(_authDirectory, LockFileName);
        RejectReparsePointIfPresent(lockPath);
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

    private static void ValidateJournal(LoginJournal journal)
    {
        if (journal.Version != 1 ||
            !Enum.IsDefined(journal.Stage) ||
            !TryReadTransactionId(journal.TempDirectoryName, out var transactionId) ||
            journal.OldTempName != $".account-login-old-{transactionId}.tmp" ||
            journal.DisplacedName != $".account-login-displaced-{transactionId}.tmp")
        {
            throw new InvalidDataException("登录事务日志格式无效");
        }

        if (journal.Stage != LoginStage.AwaitingLogin)
        {
            ValidateReadyJournal(journal);
        }
    }

    private static void ValidateReadyJournal(LoginJournal journal)
    {
        if (journal.Version != 1 ||
            journal.Stage == LoginStage.AwaitingLogin ||
            !IsSha256(journal.ActiveHash) ||
            !IsSha256(journal.NewHash) ||
            (journal.TargetSlot is null && journal.TargetOriginalHash is not null) ||
            (journal.TargetOriginalHash is not null && !IsSha256(journal.TargetOriginalHash)) ||
            (journal.TargetSlot is not null &&
             (journal.TargetSlot == AuthAccountCatalog.CurrentSlot || !AuthAccountCatalog.IsValidSlot(journal.TargetSlot))))
        {
            throw new InvalidDataException("待导入登录事务日志格式无效");
        }
    }

    private static bool TryReadTransactionId(string directoryName, out string transactionId)
    {
        const string prefix = ".account-login-";
        transactionId = string.Empty;
        if (!directoryName.StartsWith(prefix, StringComparison.Ordinal) ||
            directoryName.Length != prefix.Length + 32)
        {
            return false;
        }

        var candidate = directoryName[prefix.Length..];
        if (!Guid.TryParseExact(candidate, "N", out _))
        {
            return false;
        }

        transactionId = candidate;
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    private static void ThrowIfRequested(LoginFaultPoint? requested, LoginFaultPoint current)
    {
        if (requested == current)
        {
            throw new LoginSimulatedCrashException(current);
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

internal enum LoginStage
{
    AwaitingLogin,
    ReadyToImport,
    ActiveMoved,
    NewPromoted,
    TargetDisplaced,
    PreviousStored,
}

internal enum LoginFaultPoint
{
    AfterActiveMoved,
    AfterNewPromoted,
    AfterTargetDisplaced,
    AfterPreviousStored,
}

internal sealed record LoginJournal(
    int Version,
    LoginStage Stage,
    string TempDirectoryName,
    string OldTempName,
    string DisplacedName,
    string? TargetSlot,
    string? ActiveHash,
    string? NewHash,
    string? TargetOriginalHash,
    DateTimeOffset CreatedAt);

internal sealed class LoginSimulatedCrashException(LoginFaultPoint point)
    : Exception($"Simulated login crash at {point}");
