using System;
using System.IO;

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
        string? env = Environment.GetEnvironmentVariable("HITODAMA_DATA_DIR");
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
                string target = Path.Combine(localApp, "HitodamaCompanion");
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

        string fallback = Path.Combine(Path.GetTempPath(), "HitodamaCompanion");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static string DatabasePath => Path.Combine(Root, "companion.db");
    public static string SettingsPath => Path.Combine(Root, "loadout.json");
    public static string AuditLogPath => Path.Combine(Root, "audit.log");
    public static string IconCacheDir => Path.Combine(Root, "icons");
}
