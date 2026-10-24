using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>Only sanitized response data may cross the logging boundary.</summary>
internal static class PlatformResponseLog
{
    private const string Hidden = "[REDACTED]";
    private static readonly JsonSerializerOptions LogJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex SensitiveName = new(
        @"^(?:(?:xf?[-_]?)?api[-_]?key|key|(?:access|refresh|id|auth|security|csrf|xsrf)[-_]?token|token|(?:client[-_]?)?secret|password|passwd|(?:proxy[-_]?)?authorization|(?:set[-_]?)?cookie|signature|sig|credentials?|session(?:[-_]?id)?|code|state|(?:code[-_]?)?verifier|code[-_]?challenge|x[-_](?:amz|goog)[-_](?:signature|credential|security[-_]token)|awsaccesskeyid|googleaccessid|key[-_]pair[-_]id|policy)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(@"(?:https?|nxm)://[^\s<>""']+", RegexOptions.IgnoreCase);
    private static readonly Regex UrlUserInfo = new(@"(?<=://)[^/\s?#<>""']+@", RegexOptions.IgnoreCase);
    private static readonly Regex UrlParameter = new(@"(?<prefix>[?&#;])(?<name>[^?&#;=\s<>""']+)=(?<value>[^&#;\s<>""']*)");
    private static readonly Regex Assignment = new(
        @"\b(?<name>[\w-]+)[""']?\s*[:=]\s*(?<value>""[^""]*""|'[^']*'|[^\s,;<>""'&#]+)",
        RegexOptions.IgnoreCase);
    private static readonly Regex Bearer = new(@"\bBearer\s+[^\s,""<>&]+", RegexOptions.IgnoreCase);

    public static async Task WriteAsync(ILogger logger, HttpRequestMessage request, HttpResponseMessage response,
        string platform, string operation, IEnumerable<string?> requestSecrets, CancellationToken cancellationToken,
        bool includeBody = true, bool preserveApiErrorCodes = false)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        // ReadAsStringAsync buffers content, leaving normal JSON parsing intact.
        // Download file streams and redirects must remain unbuffered.
        var body = includeBody ? await response.Content.ReadAsStringAsync(cancellationToken) : "[body omitted: download stream or redirect]";
        var secrets = requestSecrets.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToList();
        bool IsSensitive(string name) => SensitiveName.IsMatch(name);
        void CollectUrlSecrets(string text)
        {
            foreach (Match url in Url.Matches(text))
            {
                foreach (Match userInfo in UrlUserInfo.Matches(url.Value))
                    secrets.Add(userInfo.Value.TrimEnd('@'));
                foreach (Match parameter in UrlParameter.Matches(url.Value))
                    if (IsSensitive(System.Net.WebUtility.UrlDecode(parameter.Groups["name"].Value)))
                    {
                        secrets.Add(parameter.Groups["value"].Value);
                        secrets.Add(System.Net.WebUtility.UrlDecode(parameter.Groups["value"].Value));
                    }
            }
        }
        JsonNode? json = null;
        var isJson = false;
        try { json = JsonNode.Parse(body); isJson = true; }
        catch (JsonException) { /* HTML/plain-text failures must also be sanitized. */ }

        // An HTTP error code is diagnostic data, not an OAuth authorization code.
        bool IsDiagnosticCode(JsonNode? node, string name, JsonNode? value) => name == "code" &&
            ((ReferenceEquals(node, json) && !response.IsSuccessStatusCode &&
              value is JsonValue scalar && int.TryParse(scalar.ToString(), out var code) && code == (int)response.StatusCode) ||
             (preserveApiErrorCodes && json is JsonObject root && root["errors"] is JsonArray errors &&
              errors.Any(error => ReferenceEquals(error, node))));

        void Collect(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (IsDiagnosticCode(obj, pair.Key, pair.Value)) continue;
                    if (IsSensitive(pair.Key) && pair.Value is JsonValue value)
                        secrets.Add(value.ToString());
                    else Collect(pair.Value);
                }
            else if (node is JsonArray array)
                foreach (var item in array) Collect(item);
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) CollectUrlSecrets(text);
        }
        Collect(json);
        CollectUrlSecrets(request.RequestUri?.AbsoluteUri ?? "");
        if (!isJson) CollectUrlSecrets(body);
        var responseHeaders = response.Headers.Concat(response.Content.Headers).Concat(response.TrailingHeaders).ToArray();
        foreach (var header in request.Headers.Concat(responseHeaders))
            foreach (var value in header.Value)
                if (IsSensitive(header.Key)) secrets.Add(value);
                else CollectUrlSecrets(value);
        var knownSecrets = secrets.Where(value => value.Length > 0)
            .SelectMany(value => new[] { value, Uri.EscapeDataString(value),
                JsonEncodedText.Encode(value).ToString(), System.Net.WebUtility.HtmlEncode(value) })
            .Distinct().OrderByDescending(value => value.Length).ToArray();

        string Clean(string text)
        {
            foreach (var secret in knownSecrets) text = text.Replace(secret, Hidden, StringComparison.Ordinal);
            text = Url.Replace(text, match =>
            {
                // Keep the route and ordinary parameters visible; redact only credential values.
                var url = UrlUserInfo.Replace(match.Value, Hidden + "@");
                return UrlParameter.Replace(url, parameter =>
                    IsSensitive(System.Net.WebUtility.UrlDecode(parameter.Groups["name"].Value))
                        ? parameter.Groups["prefix"].Value + parameter.Groups["name"].Value + "=" + Hidden
                        : parameter.Value);
            });
            text = Bearer.Replace(text, "Bearer " + Hidden);
            return Assignment.Replace(text, match =>
            {
                if (!IsSensitive(match.Groups["name"].Value)) return match.Value;
                var value = match.Groups["value"];
                var replacement = value.Value[0] is '"' or '\'' ? value.Value[0] + Hidden + value.Value[0] : Hidden;
                return match.Value[..(value.Index - match.Index)] + replacement;
            });
        }
        JsonNode? Sanitize(JsonNode? node)
        {
            if (node is JsonObject obj)
                return new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(Clean(pair.Key),
                    IsDiagnosticCode(obj, pair.Key, pair.Value) ? CleanCode(pair.Value) :
                    IsSensitive(pair.Key) ? JsonValue.Create(Hidden) : Sanitize(pair.Value))));
            if (node is JsonArray array) return new JsonArray(array.Select(Sanitize).ToArray());
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(Clean(text));
            return node?.DeepClone();
        }
        JsonNode? CleanCode(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? JsonValue.Create(Clean(text)) : value?.DeepClone();
        var headers = responseHeaders
            .Select(header => new { Name = Clean(header.Key), Value = SensitiveName.IsMatch(header.Key)
                ? Hidden : Clean(string.Join(", ", header.Value)) });
        logger.LogDebug("{Platform} API response: {Operation}; {Method} {Endpoint}; HTTP {StatusCode} {ReasonPhrase}; Version={Version}; Headers={Headers}; Body={Body}",
            platform, operation, request.Method.Method, Clean(request.RequestUri?.AbsoluteUri ?? ""), (int)response.StatusCode,
            Clean(response.ReasonPhrase ?? ""), response.Version, JsonSerializer.Serialize(headers, LogJson),
            isJson ? Sanitize(json)?.ToJsonString(LogJson) ?? "null" : Clean(body));
    }
}
