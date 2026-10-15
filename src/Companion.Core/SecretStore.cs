using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Companion.Core;

/// <summary>
/// Provides secure, DPAPI-encrypted credential and secret storage under DataProtectionScope.CurrentUser.
/// Supports multi-key management, cryptographic memory zeroing, corruption quarantining, and atomic file updates.
/// </summary>
public static class SecretStore
{
    private const int MaxRetries = 10;
    private static readonly int[] RetryDelaysMs = [10, 25, 50, 100, 150, 200, 300, 400, 500, 600];
    private static readonly ConcurrentDictionary<string, ReaderWriterLockSlim> SecretLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static ReaderWriterLockSlim GetLock(string targetPath) =>
        SecretLocks.GetOrAdd(targetPath, static _ => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion));

    // Generic Multi-Key API

    /// <summary>
    /// Encrypts and atomically saves a secret value under the specified secret name.
    /// </summary>
    public static void SaveSecret(string secretName, string secretValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);

        string targetPath = GetSecretFilePath(secretName);
        var rwLock = GetLock(targetPath);
        rwLock.EnterWriteLock();
        try
        {
            SaveSecretCore(targetPath, secretValue);
        }
        finally
        {
            rwLock.ExitWriteLock();
        }
    }

    private static void SaveSecretCore(string targetPath, string secretValue)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[]? plainText = null;
        byte[]? encrypted = null;
        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            plainText = Encoding.UTF8.GetBytes(secretValue);
            encrypted = ProtectedData.Protect(plainText, optionalEntropy: null, DataProtectionScope.CurrentUser);

            WriteBytesWithRetry(temporaryPath, encrypted);
            MoveWithRetry(temporaryPath, targetPath);
        }
        finally
        {
            if (plainText is not null)
            {
                CryptographicOperations.ZeroMemory(plainText);
            }

            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (File.Exists(temporaryPath))
            {
                DeleteFileWithRetry(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Loads and decrypts the secret value for the specified secret name.
    /// If the secret file is missing, empty, or corrupted, returns null and quarantines corrupted files.
    /// </summary>
    public static string? LoadSecret(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        string targetPath = GetSecretFilePath(secretName);
        var rwLock = GetLock(targetPath);
        rwLock.EnterReadLock();

        byte[]? encrypted = null;
        byte[]? plainText = null;

        try
        {
            if (!File.Exists(targetPath))
            {
                return null;
            }

            var fileInfo = new FileInfo(targetPath);
            if (fileInfo.Length == 0)
            {
                return null;
            }

            encrypted = ReadBytesWithRetry(targetPath);
            if (encrypted.Length == 0)
            {
                return null;
            }

            plainText = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainText);
        }
        catch (CryptographicException)
        {
            QuarantineCorruptedSecret(targetPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            rwLock.ExitReadLock();

            if (plainText is not null)
            {
                CryptographicOperations.ZeroMemory(plainText);
            }

            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    /// <summary>
    /// Deletes the secret file associated with the given secret name.
    /// </summary>
    public static bool DeleteSecret(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        string targetPath = GetSecretFilePath(secretName);
        var rwLock = GetLock(targetPath);
        rwLock.EnterWriteLock();
        try
        {
            return DeleteFileWithRetry(targetPath);
        }
        finally
        {
            rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns true if a non-empty secret file exists for the specified name.
    /// </summary>
    public static bool HasSecret(string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return false;
        }

        string targetPath = GetSecretFilePath(secretName);
        var rwLock = GetLock(targetPath);
        rwLock.EnterReadLock();
        try
        {
            if (!File.Exists(targetPath))
            {
                return false;
            }

            return new FileInfo(targetPath).Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Enumerates all secret names currently stored.
    /// </summary>
    public static IReadOnlyList<string> ListSecretNames()
    {
        string root = AppPaths.Root;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(Path.Combine(root, "gemini_key.bin")))
        {
            names.Add("gemini");
        }

        foreach (string file in Directory.EnumerateFiles(root, "secret_*.bin"))
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("secret_", StringComparison.OrdinalIgnoreCase))
            {
                string secretName = fileName["secret_".Length..];
                if (!string.IsNullOrWhiteSpace(secretName))
                {
                    names.Add(secretName);
                }
            }
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Backward-Compatible Facade (Gemini API Key)

    public static void SaveApiKey(string apiKey) => SaveSecret("gemini", apiKey);

    public static string? LoadApiKey() => LoadSecret("gemini");

    public static void DeleteApiKey() => DeleteSecret("gemini");

    public static bool HasApiKey() => HasSecret("gemini");

    // Internal Helpers

    public static string GetSecretFilePath(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        string normalized = secretName.Trim().ToLowerInvariant();
        if (normalized is "gemini" or "gemini_key" or "gemini_api_key")
        {
            return Path.Combine(AppPaths.Root, "gemini_key.bin");
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var clean = new string(normalized.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray());
        return Path.Combine(AppPaths.Root, $"secret_{clean}.bin");
    }

    private static void QuarantineCorruptedSecret(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string quarantine = path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
                MoveWithRetry(path, quarantine);
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
        catch
        {
            // Suppress secondary failures during quarantine
        }
    }

    private static bool DeleteFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (attempt < MaxRetries - 1 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]);
            }
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteBytesWithRetry(string path, byte[] bytes)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries - 1 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]);
            }
        }

        using var finalFs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        finalFs.Write(bytes);
        finalFs.Flush(flushToDisk: true);
    }

    private static byte[] ReadBytesWithRetry(string path)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex) when (attempt < MaxRetries - 1 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]);
            }
        }

        using var finalStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var finalBuffer = new MemoryStream();
        finalStream.CopyTo(finalBuffer);
        return finalBuffer.ToArray();
    }

    private static void MoveWithRetry(string source, string destination)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (File.Exists(destination))
                {
                    try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
                }
                try
                {
                    File.Move(source, destination, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    File.Copy(source, destination, overwrite: true);
                    try { File.Delete(source); } catch { }
                    return;
                }
            }
            catch (Exception exception) when (
                attempt < MaxRetries - 1 &&
                exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]);
            }
        }

        if (File.Exists(destination))
        {
            try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
        }
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch
        {
            File.Copy(source, destination, overwrite: true);
            try { File.Delete(source); } catch { }
        }
    }
}
