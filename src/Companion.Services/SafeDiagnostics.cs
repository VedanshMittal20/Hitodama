using System.Globalization;
using System.IO;
using Companion.Core;

namespace Companion.Services;

/// <summary>
/// Optional, redacted diagnostics for voice and desktop interaction flows.
/// Enable for a process by setting VOID_DIAGNOSTICS=1 before launch.
/// </summary>
public static class SafeDiagnostics
{
    private static readonly object Sync = new();
    private const string EnvironmentVariable = "VOID_DIAGNOSTICS";

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    public static void WriteEvent(string component, string eventName)
    {
        if (!IsEnabled)
        {
            return;
        }

        Append($"[{DateTime.UtcNow:O}] {SafeToken(component)}.{SafeToken(eventName)}");
    }

    public static void WriteMetric(string component, string metricName, long value)
    {
        if (!IsEnabled)
        {
            return;
        }

        Append($"[{DateTime.UtcNow:O}] {SafeToken(component)}.{SafeToken(metricName)}={value.ToString(CultureInfo.InvariantCulture)}");
    }

    public static void WriteFailure(string component, string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsEnabled)
        {
            return;
        }

        Append($"[{DateTime.UtcNow:O}] {SafeToken(component)}.{SafeToken(operation)} " +
               $"exception={SafeToken(exception.GetType().Name)} hresult=0x{exception.HResult:X8}");
    }

    private static string SafeToken(string value)
    {
        char[] safe = value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(48)
            .ToArray();
        return safe.Length == 0 ? "unknown" : new string(safe);
    }

    private static void Append(string line)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(Path.Combine(AppPaths.Root, "diagnostics.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never affect normal app behavior.
        }
    }
}
