using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.Core;

/// <summary>Reads the former style name from saved loadouts while always writing the Void name.</summary>
public sealed class SoulVisualStyleJsonConverter : JsonConverter<SoulVisualStyle>
{
    public override SoulVisualStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (string.Equals(value, "HitodamaFlame", StringComparison.OrdinalIgnoreCase))
            {
                return SoulVisualStyle.VoidFlame;
            }

            if (Enum.TryParse(value, ignoreCase: true, out SoulVisualStyle parsed))
            {
                return parsed;
            }
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int numericValue) &&
                 Enum.IsDefined(typeof(SoulVisualStyle), numericValue))
        {
            return (SoulVisualStyle)numericValue;
        }

        throw new JsonException("The saved visual style is not recognized.");
    }

    public override void Write(Utf8JsonWriter writer, SoulVisualStyle value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
