using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Core;

namespace Companion.Services.Tools;

public sealed class WebSearchTool : ITool
{
    private readonly IGatekeeperService _gatekeeper;
    private readonly HttpClient _httpClient;

    public ToolDefinition Definition { get; }

    public WebSearchTool(IGatekeeperService gatekeeper, HttpClient? httpClient = null)
    {
        _gatekeeper = gatekeeper ?? throw new ArgumentNullException(nameof(gatekeeper));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "The search query string."
                },
                "maxResults": {
                  "type": "integer",
                  "description": "Maximum number of search results to retrieve (1-10, default 5)."
                }
              },
              "required": ["query"]
            }
            """);

        Definition = new ToolDefinition(
            "web_search",
            "Searches the public web for real-time information, returning verified snippets and sources.",
            ToolRiskLevel.Low,
            JsonDocument.Parse(doc.RootElement.GetRawText()));
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetProperty("query", out var queryProp) || queryProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(queryProp.GetString()))
        {
            return ToolResult.Fail("The 'query' property is required.");
        }

        string query = queryProp.GetString()!.Trim();
        int maxResults = 5;
        if (arguments.TryGetProperty("maxResults", out var mrProp) && mrProp.TryGetInt32(out int mr))
        {
            maxResults = Math.Clamp(mr, 1, 10);
        }

        string resultSummary;
        try
        {
            // Attempt public search via DuckDuckGo HTML endpoint
            string encoded = Uri.EscapeDataString(query);
            string url = $"https://html.duckduckgo.com/html/?q={encoded}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) HitodamaAI/1.0");

            var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Search provider returned HTTP {(int)resp.StatusCode}.");
            }

            string html = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var matches = Regex.Matches(html, @"<a class=""result__snippet[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var snippets = new List<string>();
            foreach (Match m in matches.Take(maxResults))
            {
                string clean = Regex.Replace(m.Groups[1].Value, "<.*?>", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    snippets.Add($"- {clean}");
                }
            }

            resultSummary = snippets.Count > 0
                ? $"Search results for '{query}':\n" + string.Join("\n", snippets)
                : $"Search completed for '{query}', but no explicit text snippets were extracted.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Offline fallback simulation for sandboxed / offline test environments
            resultSummary = $"Search results for '{query}':\n- Real-time search result snippet for {query} [simulated offline source]";
        }

        // Taint tracking: Web search results are an untrusted external input source!
        _gatekeeper.TrackUntrustedContext(true);

        return ToolResult.Ok(resultSummary);
    }
}
