using System.IO;
using System.Runtime.InteropServices;

namespace Companion.Services;

public sealed class WallpaperService
{
    private const uint SpiSetDesktopWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);

    public IReadOnlyList<string> GetAvailableWallpapers()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallpapers");

        if (!Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp")
                .Take(100)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public bool ApplyWallpaper(string wallpaperPath)
    {
        ArgumentNullException.ThrowIfNull(wallpaperPath);

        if (!File.Exists(wallpaperPath))
        {
            return false;
        }

        return SystemParametersInfo(
            SpiSetDesktopWallpaper,
            0,
            wallpaperPath,
            SpifUpdateIniFile | SpifSendChange);
    }
}
