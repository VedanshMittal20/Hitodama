using System;
using System.IO;
using System.Threading;

namespace Companion.Core;

public static class AppPaths
{
    static AppPaths()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(IconCacheDir);
        }
        catch
        {
            // Best effort initialization
        }
    }

    public static string Root { get; } = DetermineRoot();

    private static string DetermineRoot()
    {
        string? env = Environment.GetEnvironmentVariable("VOID_DATA_DIR")
            ?? Environment.GetEnvironmentVariable("HITODAMA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                Directory.CreateDirectory(env);
                return env;
            }
            catch { }
        }

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApp))
        {
            try
            {
                string target = GetBrandedRoot(localApp);
                Directory.CreateDirectory(target);
                string testFile = Path.Combine(target, ".perm_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return target;
            }
            catch
            {
                // Access denied or sandboxed path
            }
        }

        string fallback = GetBrandedRoot(Path.GetTempPath());
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string GetBrandedRoot(string parentDirectory)
    {
        string target = Path.Combine(parentDirectory, "VoidCompanion");
        string legacy = Path.Combine(parentDirectory, "HitodamaCompanion");
        bool targetExists = Directory.Exists(target);
        if (!Directory.Exists(legacy))
        {
            return target;
        }

        // Keep using the original location while an older process may still be writing to it.
        if (IsExistingShellInstance())
        {
            return targetExists ? target : legacy;
        }

        string staging = target + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            using var migrationMutex = new Mutex(false, "Local\\Void.DataMigration");
            if (!migrationMutex.WaitOne(TimeSpan.FromSeconds(2)))
            {
                return legacy;
            }

            try
            {
                if (Directory.Exists(target))
                {
                    CopyMissingFiles(legacy, target);
                    return target;
                }

                CopyDirectory(legacy, staging);
                Directory.Move(staging, target);
                return target;
            }
            finally
            {
                migrationMutex.ReleaseMutex();
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch { }

            // Falling back preserves access to existing settings if the copy cannot complete.
            return Directory.Exists(target) ? target : legacy;
        }
    }

    private static bool IsExistingShellInstance()
    {
        try
        {
            using var existing = Mutex.OpenExisting("Companion.SingleInstance");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (FileInfo file in new DirectoryInfo(source).EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string target = Path.Combine(destination, file.Name);
            File.Copy(file.FullName, target, overwrite: false);
        }

        foreach (DirectoryInfo child in new DirectoryInfo(source).EnumerateDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string target = Path.Combine(destination, child.Name);
            CopyDirectory(child.FullName, target);
        }
    }

    private static void CopyMissingFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (FileInfo file in new DirectoryInfo(source).EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string target = Path.Combine(destination, file.Name);
            if (!File.Exists(target))
            {
                File.Copy(file.FullName, target, overwrite: false);
            }
        }

        foreach (DirectoryInfo child in new DirectoryInfo(source).EnumerateDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                CopyMissingFiles(child.FullName, Path.Combine(destination, child.Name));
            }
        }
    }

    public static string DatabasePath => Path.Combine(Root, "companion.db");
    public static string SettingsPath => Path.Combine(Root, "loadout.json");
    public static string AuditLogPath => Path.Combine(Root, "audit.log");
    public static string IconCacheDir => Path.Combine(Root, "icons");
}
