using System.Diagnostics;
using System.IO;

namespace Companion.Services;

public sealed record AppShortcut(string Name, string Path);

public sealed class LauncherService
{
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
        string trimmed = query?.Trim() ?? string.Empty;

        return shortcuts
            .Select(app => (App: app, Score: SubsequenceScore(trimmed, app.Name)))
            .Where(result => result.Score >= 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .Select(result => result.App)
            .ToArray();
    }

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
            Process.Start(new ProcessStartInfo(shortcut.Path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
