using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.Core;

public static class LoadoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static SoulLoadout Load()
    {
        if (!File.Exists(AppPaths.SettingsPath))
        {
            return new SoulLoadout();
        }

        string json = File.ReadAllText(AppPaths.SettingsPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<SoulLoadout>(json, JsonOptions)
            ?? throw new InvalidDataException("The saved Hitodama loadout is empty.");
    }

    public static void Save(SoulLoadout loadout)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        string temporaryPath = AppPaths.SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(loadout, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, AppPaths.SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
