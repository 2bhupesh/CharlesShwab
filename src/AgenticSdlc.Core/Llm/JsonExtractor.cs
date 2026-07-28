using System.Text.Json;

namespace AgenticSdlc.Core.Llm;

/// <summary>
/// Best-effort extraction of a single JSON object from a model response. Models wrap JSON in prose
/// or code fences and occasionally emit trailing commas; this recovers the payload before the agent
/// layer escalates to a reparse retry (spec §7.2, R-3).
/// </summary>
public static class JsonExtractor
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static JsonSerializerOptions SerializerOptions => Options;

    /// <summary>
    /// Attempts to parse <paramref name="raw"/> into <typeparamref name="T"/>. Strips code fences,
    /// isolates the first balanced <c>{...}</c> object, then deserializes permissively.
    /// </summary>
    public static (bool Ok, T? Value, string? Error) TryParse<T>(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (false, default, "empty response");

        var candidate = ExtractJsonObject(raw);
        if (candidate is null)
            return (false, default, "no JSON object found in response");

        try
        {
            var value = JsonSerializer.Deserialize<T>(candidate, Options);
            if (value is null)
                return (false, default, "deserialized to null");
            return (true, value, null);
        }
        catch (JsonException ex)
        {
            return (false, default, ex.Message);
        }
    }

    /// <summary>
    /// Returns the substring spanning the first top-level JSON object, honouring string literals and
    /// escapes so braces inside strings do not throw off the brace counter. Returns null if none.
    /// </summary>
    public static string? ExtractJsonObject(string raw)
    {
        var text = StripFences(raw).Trim();
        var start = text.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null; // unbalanced
    }

    /// <summary>Removes a leading <c>```json</c>/<c>```</c> fence and its closing fence, if present.</summary>
    private static string StripFences(string raw)
    {
        var t = raw.Trim();
        if (!t.StartsWith("```")) return raw;

        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return raw;
        var body = t[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing] : body;
    }
}
