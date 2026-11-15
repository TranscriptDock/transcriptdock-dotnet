using System.Net;
using System.Text.Json;
using TranscriptDock;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task<T> Raises<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
static void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Expected argument validation");
}

using var client = new TranscriptDockClient("td_live_fixture_only", Environment.GetEnvironmentVariable("TRANSCRIPTDOCK_TEST_BASE_URL")!);
var body = new { source = new { url = "https://example.com/video" }, mode = "captions_only", max_credits = 1 };
for (int i = 0; i < 2; i++)
{
    var job = await client.CreateJobAsync(body, "fixture-replay-01");
    Check(job.GetProperty("future_field").GetInt32() == 42, "future response field lost");
    var request = job.GetProperty("request");
    Check(request.GetProperty("authorized").GetBoolean() && request.GetProperty("idempotency_key").GetString() == "fixture-replay-01", "headers changed");
    Check(request.GetProperty("body").GetProperty("max_credits").GetInt32() == 1 && request.GetProperty("body").GetProperty("mode").GetString() == "captions_only", "credit ceiling or mode changed");
}
var listed = await client.RequestAsync(HttpMethod.Get, "/v1/jobs", query: new Dictionary<string, string?> { ["cursor"] = "a/b?c&d", ["limit"] = "7" });
Check(listed.GetProperty("request").GetProperty("query").GetProperty("cursor")[0].GetString() == "a/b?c&d", "cursor encoding changed");
var escaped = await client.GetJobAsync("id/other?x");
Check(escaped.GetProperty("request").GetProperty("path").GetString()!.EndsWith("id%2Fother%3Fx"), "ID encoding changed");
var transcript = await client.GetTranscriptAsync("fixture");
Check(transcript.GetProperty("words")[0].GetProperty("start_ms").GetInt32() == 123, "timing unit changed");
Check((await client.ExportTranscriptAsync("fixture", "srt")).Contains("Café fixture"), "UTF-8 export lost");
foreach (var status in new[] { "succeeded", "failed", "cancelled" })
    Check((await client.WaitForJobAsync(status)).GetProperty("status").GetString() == status, "terminal status lost");
var timed = await Raises<JobTimeoutException>(async () => await client.WaitForJobAsync("processing", TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1)));
Check(timed.Job!.Value.GetProperty("status").GetString() == "processing", "timeout lost last job");
var error = await Raises<ApiException>(async () => await client.RequestAsync(HttpMethod.Get, "/v1/rate-limit"));
Check(error.Status == 429 && error.Retryable && error.RetryAfterSeconds == 4 && error.RequestId == "fixture-request", "API error fields lost");
Check(!error.Message.Contains("td_live_fixture_only"), "key in error");
Check((await Raises<ApiException>(async () => await client.RequestAsync(HttpMethod.Get, "/v1/redirect"))).Status == 302, "redirect followed");
foreach (var path in new[] { "gateway", "error-shape", "malformed", "array" })
    await Raises<ApiException>(async () => await client.RequestAsync(HttpMethod.Get, "/v1/" + path));
foreach (var path in new[] { "/v1/batches", "/v1/video-discoveries", "/v1/language-discoveries" })
{
    await Raises<ArgumentException>(async () => await client.RequestAsync(HttpMethod.Post, path, new { }));
    var result = await client.RequestAsync(HttpMethod.Post, path, new { fixture = true }, idempotencyKey: "fixture-generic-01");
    Check(result.GetProperty("request").GetProperty("method").GetString() == "POST", "generic submission failed");
}
Check((await client.RequestAsync(HttpMethod.Delete, "/v1/transcripts/fixture")).GetProperty("request").GetProperty("method").GetString() == "DELETE", "delete dispatch failed");
foreach (var url in new[] { "http://example.com", "https://user:pass@example.com", "https://example.com/path", "https://example.com?x=1" })
    Reject(() => new TranscriptDockClient("fixture-key", url));
Reject(() => new TranscriptDockClient("bad\nkey"));
await Raises<ArgumentException>(async () => await client.CreateJobAsync(new { }, "bad\nreplay"));
await Raises<ArgumentException>(async () => await client.RequestAsync(HttpMethod.Get, "/v1/../secret"));
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    await Raises<OperationCanceledException>(async () => await client.CapabilitiesAsync(cancel.Token));
}
using (var shortTimeout = new TranscriptDockClient("fixture-key", Environment.GetEnvironmentVariable("TRANSCRIPTDOCK_TEST_BASE_URL")!, TimeSpan.FromMilliseconds(30)))
    Check((await Raises<ApiException>(async () => await shortTimeout.RequestAsync(HttpMethod.Get, "/v1/slow"))).Code == "NETWORK_ERROR", "transport timeout not handled");
await Raises<ArgumentException>(async () => await client.RequestAsync(new HttpMethod("post"), "/v1/jobs", new { }));
await Raises<ArgumentException>(async () => await client.RequestAsync(HttpMethod.Get, "/v1/%2e%2e/secret"));
foreach (var (path, status, code) in new[] { ("budget", 402, "BUDGET_EXCEEDED"), ("conflict", 409, "IDEMPOTENCY_CONFLICT"), ("jobs/not-owned", 404, "NOT_FOUND") })
{
    var rejected = await Raises<ApiException>(async () => await client.RequestAsync(HttpMethod.Get, "/v1/" + path));
    Check(rejected.Status == status && rejected.Code == code && !rejected.Retryable, "budget, replay conflict or inaccessible job error lost");
}

Console.WriteLine(".NET client checks passed");
