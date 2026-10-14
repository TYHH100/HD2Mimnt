using System.Net;
using System.Text.Json;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Downloads;

/// <summary>Rejects login/error documents even when an endpoint reports HTTP 200.</summary>
internal sealed class DirectDownloadResponseGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int ProbeLimit = 16 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = await base.SendAsync(request, token);
        try
        {
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var attachment = string.Equals(response.Content.Headers.ContentDisposition?.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase);
            var json = type == "application/json" || type?.EndsWith("+json", StringComparison.Ordinal) == true;
            // File attachments (including legitimate JSON files) are not API envelopes.
            if (!attachment && (json || type is null or "text/plain") && response.Content.Headers.ContentLength is not > ProbeLimit)
            {
                var original = response.Content;
                var source = await original.ReadAsStreamAsync(token);
                var prefix = new byte[ProbeLimit + 1];
                var count = await source.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, token);
                if (count <= ProbeLimit && ParseFailure(prefix.AsMemory(0, count)) is { } failure)
                    throw new DirectDownloadResponseException(failure);
                // Replay the bounded prefix exactly: inspection must not truncate a real download.
                var replay = new StreamContent(new PrefixStream(prefix.AsMemory(0, count), source, original));
                foreach (var header in original.Headers) replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = replay;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new DirectDownloadResponseException(DirectDownloadResponseFailure.LoginRequired);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new DirectDownloadResponseException(DirectDownloadResponseFailure.AccessDenied);
            if (response.IsSuccessStatusCode && !attachment)
            {
                if (type is "text/html" or "application/xhtml+xml")
                    throw new DirectDownloadResponseException(DirectDownloadResponseFailure.WebPage);
                var disposition = response.Content.Headers.ContentDisposition;
                var explicitFileName = disposition?.FileNameStar ?? disposition?.FileName;
                var uri = response.RequestMessage?.RequestUri ?? request.RequestUri;
                if (json && string.IsNullOrWhiteSpace(explicitFileName) &&
                    !string.Equals(Path.GetExtension(uri?.AbsolutePath), ".json", StringComparison.OrdinalIgnoreCase))
                    throw new DirectDownloadResponseException(DirectDownloadResponseFailure.ApiResponse);
            }
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static DirectDownloadResponseFailure? ParseFailure(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var error = Find(root, "error");
            var code = Find(root, "ec", "code", "statusCode", "status_code", "status");
            var message = Find(root, "em", "message", "msg", "detail", "error_description", "title").ToString();
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (code.ValueKind == JsonValueKind.Undefined) code = Find(error, "code", "status");
                if (string.IsNullOrWhiteSpace(message)) message = Find(error, "message", "msg", "detail").ToString();
            }
            var numeric = long.TryParse(code.ToString(), out var number) ? number : 0;
            var diagnostic = (message + " " + code + " " + (error.ValueKind == JsonValueKind.String ? error.GetString() : "")).ToLowerInvariant();
            var failed = numeric is >= 400 and <= 599 or >= 40000 and <= 59999 or < 0 ||
                (code.ValueKind == JsonValueKind.String && ContainsAny(code.ToString().ToLowerInvariant(),
                    "unauthoriz", "unauthenticat", "login_required", "not_logged_in", "token_expired", "forbidden", "access_denied")) ||
                Find(root, "success", "ok").ValueKind == JsonValueKind.False ||
                Find(root, "status").ToString().ToLowerInvariant() is "error" or "failed" or "fail" ||
                error.ValueKind is JsonValueKind.Object or JsonValueKind.String;
            if (numeric is 401 or >= 40100 and <= 40199 ||
                (failed && ContainsAny(diagnostic, "登录", "登入", "login", "log in", "sign in", "unauthorized", "unauthenticated", "not_logged_in")))
                return DirectDownloadResponseFailure.LoginRequired;
            if (failed && ContainsAny(diagnostic, "过期", "過期", "expired", "expire"))
                return DirectDownloadResponseFailure.ExpiredLink;
            if (numeric is 403 or >= 40300 and <= 40399 || (failed && ContainsAny(diagnostic, "权限", "權限", "forbidden", "access denied")))
                return DirectDownloadResponseFailure.AccessDenied;
            return failed ? DirectDownloadResponseFailure.ServerError : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.Ordinal));
    private static JsonElement Find(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream source, HttpContent original) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int CopyPrefix(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.Span.Slice(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = CopyPrefix(buffer.AsSpan(offset, count)); return read > 0 ? read : source.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = CopyPrefix(buffer.Span); return read > 0 ? ValueTask.FromResult(read) : source.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
