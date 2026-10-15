using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>Only sanitized response data may cross the logging boundary.</summary>
internal static class NexusModsResponseLog
{
    private const string Hidden = "[REDACTED]";
    private static readonly JsonSerializerOptions LogJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex SensitiveName = new(
        "key|token|secret|password|authorization|cookie|signature|credential|email|user[_-]?name|user[_-]?id|member[_-]?id|avatar|profile_url|download_url|download_link|session|^ip$|^uri$|^code$|^state$|verifier",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(@"(?:https?|nxm)://[^\s<>""']+", RegexOptions.IgnoreCase);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}");
    private static readonly Regex Assignment = new(
        @"\b(api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|authorization|cookie|signature|credential|email|user[_-]?name|user[_-]?id|code|state|verifier)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;<>]+)",
        RegexOptions.IgnoreCase);
    private static readonly Regex Bearer = new(@"\bBearer\s+[^\s,""<>]+", RegexOptions.IgnoreCase);

    public static async Task WriteAsync(ILogger logger, HttpRequestMessage request, HttpResponseMessage response,
        string operation, IEnumerable<string?> requestSecrets, CancellationToken cancellationToken)
    {
        // ReadAsStringAsync buffers content, leaving normal JSON parsing intact.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var secrets = requestSecrets.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToList();
        var isAccountResponse = request.RequestUri?.AbsolutePath.EndsWith("/users/validate.json", StringComparison.Ordinal) == true;
        bool IsSensitive(string name) => SensitiveName.IsMatch(name) || (isAccountResponse && name == "name");
        JsonNode? json = null;
        var isJson = false;
        try { json = JsonNode.Parse(body); isJson = true; }
        catch (JsonException) { /* HTML/plain-text failures must also be sanitized. */ }

        // An HTTP error code is diagnostic data, not an OAuth authorization code.
        bool IsHttpErrorCode(JsonNode? node, string name, JsonNode? value) =>
            ReferenceEquals(node, json) && name == "code" && !response.IsSuccessStatusCode &&
            value is JsonValue scalar && int.TryParse(scalar.ToString(), out var code) &&
            code == (int)response.StatusCode;

        void Collect(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (IsHttpErrorCode(obj, pair.Key, pair.Value)) continue;
                    if (IsSensitive(pair.Key) && pair.Value is JsonValue value)
                        secrets.Add(value.ToString());
                    else Collect(pair.Value);
                }
            else if (node is JsonArray array)
                foreach (var item in array) Collect(item);
        }
        Collect(json);
        var knownSecrets = secrets.Where(value => value.Length > 0)
            .SelectMany(value => new[] { value, Uri.EscapeDataString(value),
                JsonEncodedText.Encode(value).ToString(), System.Net.WebUtility.HtmlEncode(value) })
            .Distinct().OrderByDescending(value => value.Length).ToArray();

        string Clean(string text)
        {
            foreach (var secret in knownSecrets) text = text.Replace(secret, Hidden, StringComparison.Ordinal);
            text = Url.Replace(text, match =>
            {
                // Signed URLs can carry credentials in either their path or query.
                return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                    ? $"{uri.Scheme}://{uri.Host}/{Hidden}" : Hidden;
            });
            text = Email.Replace(text, Hidden);
            text = Bearer.Replace(text, "Bearer " + Hidden);
            return Assignment.Replace(text, match => match.Groups[1].Value + "=" + Hidden);
        }
        JsonNode? Sanitize(JsonNode? node)
        {
            if (node is JsonObject obj)
                return new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(Clean(pair.Key),
                    IsHttpErrorCode(obj, pair.Key, pair.Value) ? pair.Value?.DeepClone() :
                    IsSensitive(pair.Key) ? JsonValue.Create(Hidden) : Sanitize(pair.Value))));
            if (node is JsonArray array) return new JsonArray(array.Select(Sanitize).ToArray());
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(Clean(text));
            return node?.DeepClone();
        }
        var headers = response.Headers.Concat(response.Content.Headers).Concat(response.TrailingHeaders)
            .Select(header => new { Name = Clean(header.Key), Value = SensitiveName.IsMatch(header.Key)
                ? Hidden : Clean(string.Join(", ", header.Value)) });
        logger.LogDebug("Nexus API response: {Operation}; {Method} {Endpoint}; HTTP {StatusCode} {ReasonPhrase}; Version={Version}; Headers={Headers}; Body={Body}",
            operation, request.Method.Method, Clean(request.RequestUri?.AbsolutePath ?? ""), (int)response.StatusCode,
            Clean(response.ReasonPhrase ?? ""), response.Version, JsonSerializer.Serialize(headers, LogJson),
            isJson ? Sanitize(json)?.ToJsonString(LogJson) ?? "null" : Clean(body));
    }
}
