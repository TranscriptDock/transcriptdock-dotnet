using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TranscriptDock;

/// <summary>Calls the public API without automatic retries or redirects. Dispose after use.</summary>
public sealed class TranscriptDockClient : IDisposable
{
    private readonly string apiKey;
    private readonly string origin;
    private readonly HttpClient http;
    private static readonly Regex TokenPattern = new(@"td_live_[A-Za-z0-9_-]+|Bearer\s+\S+", RegexOptions.IgnoreCase);

    public TranscriptDockClient(string apiKey, string baseUrl = "https://www.transcriptdock.com", TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Provide an API key without whitespace or control characters.", nameof(apiKey));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            baseUrl.Any(char.IsWhiteSpace))
            throw new ArgumentException("Use an HTTPS origin, or loopback HTTP, without credentials, path, query or fragment.", nameof(baseUrl));
        var requestTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (requestTimeout <= TimeSpan.Zero || requestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.apiKey = apiKey;
        origin = uri.GetLeftPart(UriPartial.Authority);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = requestTimeout };
    }

    /// <summary>Returns the full JSON object, retaining future fields and original timing units.</summary>
    public async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body = null,
        IReadOnlyDictionary<string, string?>? query = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var raw = await RequestTextAsync(method, path, body, query, idempotencyKey, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ApiException("INVALID_RESPONSE", "The API did not return a JSON object.");
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new ApiException("INVALID_RESPONSE", "The API did not return JSON."); }
    }

    /// <summary>Returns raw export text or calls a public endpoint without parsing its body.</summary>
    public async Task<string> RequestTextAsync(HttpMethod method, string path, object? body = null,
        IReadOnlyDictionary<string, string?>? query = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || path.IndexOfAny(['?', '#', '\\']) >= 0 ||
            Uri.UnescapeDataString(path).Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Use a /v1/ path and pass query parameters separately.", nameof(path));
        bool needsKey = method.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && (path is "/v1/jobs" or "/v1/batches" or "/v1/video-discoveries" or "/v1/language-discoveries" || path.EndsWith("/retry", StringComparison.Ordinal));
        if ((needsKey || idempotencyKey is not null) && (idempotencyKey is null || !Regex.IsMatch(idempotencyKey, @"\A[ -~]{8,128}\z")))
            throw new ArgumentException("Idempotency keys need 8 to 128 printable ASCII characters.", nameof(idempotencyKey));
        string url = origin + path;
        if (query is not null)
        {
            var encoded = string.Join("&", query.Where(pair => pair.Value is not null)
                .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!)));
            if (encoded.Length > 0) url += "?" + encoded;
        }
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("transcriptdock-dotnet/0.1.0");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw Error(raw, (int)response.StatusCode);
            return raw;
        }
        catch (HttpRequestException) { throw new ApiException("NETWORK_ERROR", "Could not reach the API. Check the job before retrying a submission.", retryable: true); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ApiException("NETWORK_ERROR", "The API request timed out. Check the job before retrying a submission.", retryable: true); }
    }

    public Task<JsonElement> CapabilitiesAsync(CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Get, "/v1/capabilities", cancellationToken: cancellationToken);
    public Task<JsonElement> CreateJobAsync(object body, string idempotencyKey, CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Post, "/v1/jobs", body, idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);
    public Task<JsonElement> GetJobAsync(string id, CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Get, "/v1/jobs/" + EncodeId(id), cancellationToken: cancellationToken);
    public Task<JsonElement> GetTranscriptAsync(string id, CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Get, "/v1/transcripts/" + EncodeId(id), cancellationToken: cancellationToken);
    public Task<string> ExportTranscriptAsync(string id, string format = "txt", CancellationToken cancellationToken = default)
    {
        if (format is not ("json" or "txt" or "srt" or "vtt")) throw new ArgumentException("Choose json, txt, srt or vtt.", nameof(format));
        return RequestTextAsync(HttpMethod.Get, "/v1/transcripts/" + EncodeId(id) + "/export",
            query: new Dictionary<string, string?> { ["format"] = format }, cancellationToken: cancellationToken);
    }

    /// <summary>Returns any terminal job. Caller cancellation propagates; polling timeout preserves the last job.</summary>
    public async Task<JsonElement> WaitForJobAsync(string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var duration = timeout ?? TimeSpan.FromSeconds(120);
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue || interval <= TimeSpan.Zero || interval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        JsonElement? last = null;
        try
        {
            while (true)
            {
                last = await GetJobAsync(id, deadline.Token);
                if (last.Value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is "succeeded" or "failed" or "cancelled")
                    return last.Value;
                await Task.Delay(interval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new JobTimeoutException(last); }
    }

    private ApiException Error(string raw, int status)
    {
        string code = "HTTP_ERROR", message = $"Request failed with status {status}.";
        string? requestId = null;
        int? retryAfter = null;
        bool retryable = status == 429 || status >= 500;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("request_id", out var request) && request.ValueKind == JsonValueKind.String) requestId = Redact(request.GetString()!);
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!;
                    if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString()!;
                    if (error.TryGetProperty("retryable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) retryable = r.GetBoolean();
                    if (error.TryGetProperty("retry_after_seconds", out var delay) && delay.ValueKind == JsonValueKind.Number && delay.TryGetInt32(out var seconds)) retryAfter = seconds;
                }
            }
        }
        catch (JsonException) { }
        return new ApiException(Redact(code), Redact(message), status, retryable, requestId, retryAfter);
    }
    private string Redact(string value) => TokenPattern.Replace(value.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal), "[REDACTED]");
    private static string EncodeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id is "." or "..") throw new ArgumentException("Provide a nonempty ID.", nameof(id));
        return Uri.EscapeDataString(id);
    }
    public void Dispose() => http.Dispose();
}
