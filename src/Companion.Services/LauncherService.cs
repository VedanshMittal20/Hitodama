using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Companion.Services;

public sealed record AppShortcut(string Name, string Path);

public sealed class LauncherService
{
    private static readonly Regex ArithmeticCharacters = new(@"^[0-9+\-*/%^().\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexColor = new(@"^(?:#|0x)(?<hex>[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$|^(?<hex>[0-9a-fA-F]{6})$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RgbColor = new(
        @"^rgb\s*\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})\s*\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex UnitConversion = new(
        @"^(?<value>[+-]?\d+(?:\.\d+)?)\s*(?<from>km|mi|m|cm|mm|in|ft|kg|g|lb|oz|°?c|°?f|celsius|fahrenheit)\s+to\s+(?<to>km|mi|m|cm|mm|in|ft|kg|g|lb|oz|°?c|°?f|celsius|fahrenheit)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public IReadOnlyList<AppShortcut> GetInstalledShortcuts()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] startMenuDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];

        foreach (string root in startMenuDirectories.Where(Directory.Exists))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    paths.Add(path);
                }
            }
            catch
            {
                // Access issues or corrupt directories silently ignored
            }
        }

        return paths
            .Select(path => new AppShortcut(Path.GetFileNameWithoutExtension(path), path))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<AppShortcut> Search(IReadOnlyList<AppShortcut> shortcuts, string query, int maxResults = 30)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        if (maxResults <= 0)
        {
            return Array.Empty<AppShortcut>();
        }

        string trimmed = query?.Trim() ?? string.Empty;

        bool hasInstantAnswer = TryEvaluateInstantAnswer(trimmed, out AppShortcut? instantAnswer);

        IEnumerable<AppShortcut> matches = shortcuts
            .Select(app => (App: app, Score: SubsequenceScore(trimmed, app.Name)))
            .Where(result => result.Score >= 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .Select(result => result.App)
            .ToArray();
        if (!hasInstantAnswer || instantAnswer is null)
        {
            return matches.ToArray();
        }

        return new[] { instantAnswer }
            .Concat(matches.Where(app => !string.Equals(app.Path, instantAnswer.Path, StringComparison.OrdinalIgnoreCase)))
            .Take(maxResults)
            .ToArray();
    }

    public bool TryEvaluateInstantAnswer(string query, out AppShortcut? result)
    {
        result = null;
        string trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > 160)
        {
            return false;
        }

        if (trimmed.StartsWith('>'))
        {
            string command = trimmed[1..].Trim();
            if (command.Length == 0)
            {
                return false;
            }

            result = new AppShortcut($"⚡ Run: {command}", $"cmd:{command}");
            return true;
        }

        if (TryConvertColor(trimmed, out string colorAnswer) ||
            TryConvertUnit(trimmed, out colorAnswer) ||
            TryCalculate(trimmed, out colorAnswer))
        {
            result = new AppShortcut($"= {colorAnswer}  (Click to Copy)", $"math:{colorAnswer}");
            return true;
        }

        return false;
    }

    private static bool TryCalculate(string expression, out string answer)
    {
        answer = string.Empty;
        if (expression.Length > 128 ||
            !ArithmeticCharacters.IsMatch(expression) ||
            !expression.Any(character => character is '+' or '-' or '*' or '/' or '%' or '^'))
        {
            return false;
        }

        try
        {
            using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            object? computed = table.Compute(expression, string.Empty);
            if (computed is null || computed is DBNull)
            {
                return false;
            }

            double numeric = Convert.ToDouble(computed, CultureInfo.InvariantCulture);
            if (!double.IsFinite(numeric))
            {
                return false;
            }

            answer = Convert.ToString(computed, CultureInfo.InvariantCulture) ?? numeric.ToString("G", CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertColor(string query, out string answer)
    {
        answer = string.Empty;
        Match hexMatch = HexColor.Match(query);
        if (hexMatch.Success)
        {
            string hex = hexMatch.Groups["hex"].Value;
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(character => new string(character, 2)));
            }

            byte red = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte green = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte blue = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            answer = $"RGB({red}, {green}, {blue})";
            return true;
        }

        Match rgbMatch = RgbColor.Match(query);
        if (!rgbMatch.Success ||
            !byte.TryParse(rgbMatch.Groups["r"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(rgbMatch.Groups["g"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(rgbMatch.Groups["b"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        answer = $"#{r:X2}{g:X2}{b:X2}";
        return true;
    }

    private static bool TryConvertUnit(string query, out string answer)
    {
        answer = string.Empty;
        Match match = UnitConversion.Match(query);
        if (!match.Success ||
            !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }

        string from = match.Groups["from"].Value.ToLowerInvariant();
        string to = match.Groups["to"].Value.ToLowerInvariant();
        if (!TryGetUnitDimension(from, out string? fromDimension) ||
            !TryGetUnitDimension(to, out string? toDimension) ||
            fromDimension != toDimension)
        {
            return false;
        }

        double converted;
        if (fromDimension == "temperature")
        {
            double celsius = from is "f" or "°f" or "fahrenheit" ? (value - 32) * 5 / 9 : value;
            converted = to is "f" or "°f" or "fahrenheit" ? celsius * 9 / 5 + 32 : celsius;
        }
        else
        {
            converted = value * GetUnitFactor(from) / GetUnitFactor(to);
        }

        if (!double.IsFinite(converted))
        {
            return false;
        }

        answer = $"{converted.ToString("0.###", CultureInfo.InvariantCulture)} {to}";
        return true;
    }

    private static bool TryGetUnitDimension(string unit, out string dimension)
    {
        dimension = unit switch
        {
            "km" or "mi" or "m" or "cm" or "mm" or "in" or "ft" => "distance",
            "kg" or "g" or "lb" or "oz" => "mass",
            "c" or "°c" or "celsius" or "f" or "°f" or "fahrenheit" => "temperature",
            _ => string.Empty
        };
        return dimension.Length > 0;
    }

    private static double GetUnitFactor(string unit) => unit switch
    {
        "km" => 1000,
        "mi" => 1609.344,
        "m" => 1,
        "cm" => 0.01,
        "mm" => 0.001,
        "in" => 0.0254,
        "ft" => 0.3048,
        "kg" => 1,
        "g" => 0.001,
        "lb" => 0.45359237,
        "oz" => 0.028349523125,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    public static int SubsequenceScore(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        int matched = 0;
        int score = 0;
        int previousIndex = -1;

        foreach (char character in query)
        {
            int index = candidate.IndexOf(character.ToString(), previousIndex + 1, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            score += previousIndex + 1 == index ? 3 : 1;
            if (index == 0 || !char.IsLetterOrDigit(candidate[index - 1]))
            {
                score += 2;
            }

            previousIndex = index;
            matched++;
        }

        return score + (matched * 2) - candidate.Length / 20;
    }

    public bool Launch(AppShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        try
        {
            if (shortcut.Path.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k {shortcut.Path[4..]}") { UseShellExecute = true });
            }
            else if (!shortcut.Path.StartsWith("math:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(shortcut.Path) { UseShellExecute = true });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
