using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Companion.Core;

namespace Companion.Services;

public sealed class GeminiVisionService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> AskAboutScreenAsync(
        byte[] jpegImage,
        byte[] wavAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpegImage);
        ArgumentNullException.ThrowIfNull(wavAudio);
        if (jpegImage.Length == 0 || wavAudio.Length <= 44)
        {
            throw new ArgumentException("A screen image and recorded question are required.");
        }

        string apiKey = SecretStore.LoadApiKey()
            ?? throw new InvalidOperationException("Add a Gemini API key in Void Studio before using Circle to Ask.");
        string endpoint =
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var payload = new
        {
            system_instruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = "You are Void, a concise desktop assistant. Answer the user's spoken question using the attached screenshot as context. Do not claim to see anything outside the image."
                    }
                }
            },
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(jpegImage) } },
                        new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(wavAudio) } }
                    }
                }
            }
        };

        using var response = await HttpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Gemini screen request failed ({(int)response.StatusCode}): {responseText}",
                null,
                response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0 ||
            !candidates[0].TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts))
        {
            throw new InvalidDataException("Gemini returned a response without answer content.");
        }

        string answer = string.Join(
            Environment.NewLine,
            parts.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)))
            .Trim();
        return string.IsNullOrEmpty(answer)
            ? throw new InvalidDataException("Gemini returned an empty answer.")
            : answer;
    }
}
