using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using Companion.Core;

namespace Companion.Services.Tools;

public sealed class FileReadWriteTool : ITool
{
    private const int MaxReadBytes = 2 * 1024 * 1024; // 2 MB
    private const int MaxWriteBytes = 5 * 1024 * 1024; // 5 MB

    public ToolDefinition Definition { get; }

    public FileReadWriteTool()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["read_text", "write_text", "list_directory", "file_exists"],
                  "description": "File system action to perform."
                },
                "path": {
                  "type": "string",
                  "description": "Target file or directory path."
                },
                "content": {
                  "type": "string",
                  "description": "Text content to write (for write_text)."
                },
                "overwrite": {
                  "type": "boolean",
                  "description": "Whether to overwrite existing file in write_text (default true)."
                },
                "maxLines": {
                  "type": "integer",
                  "description": "Maximum lines to read (default 200, max 1000)."
                }
              },
              "required": ["action", "path"]
            }
            """);

        Definition = new ToolDefinition(
            "file_read_write",
            "Performs secured file system operations: read_text, write_text, list_directory, and file_exists.",
            ToolRiskLevel.Medium,
            JsonDocument.Parse(doc.RootElement.GetRawText()));
    }

    public ToolRiskLevel AssessRisk(JsonElement arguments, ToolExecutionContext context)
    {
        if (arguments.TryGetProperty("action", out var actionProp))
        {
            string? action = actionProp.GetString();
            if (string.Equals(action, "write_text", StringComparison.OrdinalIgnoreCase))
            {
                return ToolRiskLevel.Medium;
            }

            if (context.IsUntrustedInput)
            {
                // Reading files under untrusted context could leak sensitive user files
                return ToolRiskLevel.Medium;
            }

            return ToolRiskLevel.Low;
        }

        return ToolRiskLevel.Medium;
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetProperty("action", out var actionProp) || actionProp.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Fail("The 'action' property is required.");
        }

        if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(pathProp.GetString()))
        {
            return ToolResult.Fail("The 'path' property is required.");
        }

        string action = actionProp.GetString()!.Trim().ToLowerInvariant();
        string rawPath = pathProp.GetString()!.Trim();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawPath);
            ValidatePathSecurity(fullPath);
        }
        catch (SecurityException ex)
        {
            return ToolResult.Fail($"Access denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Invalid path: {ex.Message}");
        }

        try
        {
            switch (action)
            {
                case "read_text":
                    if (!File.Exists(fullPath))
                    {
                        return ToolResult.Fail($"File not found: {fullPath}");
                    }

                    var info = new FileInfo(fullPath);
                    if (info.Length > MaxReadBytes)
                    {
                        return ToolResult.Fail($"File size exceeds maximum allowed read size of 2 MB ({info.Length} bytes).");
                    }

                    int maxLines = 200;
                    if (arguments.TryGetProperty("maxLines", out var mlProp) && mlProp.TryGetInt32(out int ml))
                    {
                        maxLines = Math.Clamp(ml, 1, 1000);
                    }

                    var lines = new List<string>();
                    using (var reader = new StreamReader(fullPath))
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null && lines.Count < maxLines)
                        {
                            lines.Add(line);
                        }
                    }

                    return ToolResult.Ok(string.Join("\n", lines));

                case "write_text":
                    string content = arguments.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String
                        ? contentProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
                    {
                        return ToolResult.Fail($"Content exceeds maximum allowed write size of 5 MB.");
                    }

                    bool overwrite = true;
                    if (arguments.TryGetProperty("overwrite", out var owProp) && owProp.ValueKind is (JsonValueKind.True or JsonValueKind.False))
                    {
                        overwrite = owProp.GetBoolean();
                    }

                    if (File.Exists(fullPath) && !overwrite)
                    {
                        return ToolResult.Fail($"File already exists and overwrite is set to false: {fullPath}");
                    }

                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
                    return ToolResult.Ok($"Successfully wrote {content.Length} characters to {fullPath}.");

                case "list_directory":
                    if (!Directory.Exists(fullPath))
                    {
                        return ToolResult.Fail($"Directory not found: {fullPath}");
                    }

                    var entries = new List<string>();
                    foreach (var d in Directory.EnumerateDirectories(fullPath).Take(100))
                    {
                        entries.Add($"[DIR]  {Path.GetFileName(d)}");
                    }
                    foreach (var f in Directory.EnumerateFiles(fullPath).Take(100))
                    {
                        entries.Add($"[FILE] {Path.GetFileName(f)}");
                    }

                    return ToolResult.Ok(string.Join("\n", entries));

                case "file_exists":
                    bool exists = File.Exists(fullPath) || Directory.Exists(fullPath);
                    return ToolResult.Ok(exists ? "true" : "false");

                default:
                    return ToolResult.Fail($"Unknown action: '{action}'.");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"File operation '{action}' failed: {ex.Message}");
        }
    }

    private static void ValidatePathSecurity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string normalized = fullPath.Replace('/', '\\').TrimEnd('\\');

        // Guard critical Windows system and credential locations using strict directory/file boundaries
        string[] forbiddenBoundaries =
        [
            @"Windows\System32",
            @"Windows\System",
            @"system32\config",
            @"sam",
            @".ssh",
            @".aws",
            @"AppData\Local\Microsoft\Credentials",
            @"AppData\Roaming\Microsoft\Credentials"
        ];

        foreach (string boundary in forbiddenBoundaries)
        {
            if (MatchesPathBoundary(normalized, boundary))
            {
                throw new SecurityException($"Access to sensitive system or credential path is blocked: '{path}'.");
            }
        }
    }

    private static bool MatchesPathBoundary(string normalizedPath, string target)
    {
        string cleanTarget = target.Trim('\\');
        string midPattern = "\\" + cleanTarget + "\\";
        string endPattern = "\\" + cleanTarget;

        return normalizedPath.IndexOf(midPattern, StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalizedPath.EndsWith(endPattern, StringComparison.OrdinalIgnoreCase);
    }
}
