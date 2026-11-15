# Transcript Dock for .NET

An asynchronous client for the public Transcript Dock API. Targets .NET 8 and
newer, with no third party runtime dependencies. Version 0.1.0 is available on
[NuGet](https://www.nuget.org/packages/TranscriptDock/0.1.0).

```sh
dotnet add package TranscriptDock --version 0.1.0
```

```csharp
using TranscriptDock;

using var client = new TranscriptDockClient(Environment.GetEnvironmentVariable("TRANSCRIPTDOCK_API_KEY")!);
// Generate once per intended submission and save it with the returned job ID.
var key = Guid.NewGuid().ToString();
var job = await client.CreateJobAsync(new {
    source = new { url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" },
    mode = "captions_only", max_credits = 1
}, key);
job = await client.WaitForJobAsync(job.GetProperty("id").GetString()!);
if (job.GetProperty("status").GetString() == "succeeded") {
    var text = await client.ExportTranscriptAsync(job.GetProperty("result_id").GetString()!, "txt");
    Console.WriteLine(text);
}
```

Creating jobs can spend credits. The example caps captions at one credit and
does not enable AI fallback. Reuse the exact request body and saved idempotency
key to recover a lost submission response. There are no automatic retries.

`CapabilitiesAsync`, `CreateJobAsync`, `GetJobAsync`, `GetTranscriptAsync`,
`ExportTranscriptAsync` and `WaitForJobAsync` cover the core flow.
`RequestAsync(method, path, body, query, idempotencyKey, cancellationToken)`
covers all public `/v1` endpoints using the exact API fields, including batches,
discovery, uploads, usage and webhooks. See the
[API contract](https://www.transcriptdock.com/docs/openapi.yaml).

JSON responses are cloned `JsonElement` objects retaining every field and the
original timing units. Export methods return raw text, including JSON exports.
Polling returns failed and cancelled jobs normally; inspect status first.
`JobTimeoutException.Job` retains the last observed job. Caller cancellation
propagates as `OperationCanceledException`. Dispose the client when finished.

The default request timeout is 30 seconds. Custom origins require HTTPS, except
loopback HTTP development. Redirects are blocked to protect the API key.
`ApiException` exposes `Code`, `Status`, `Retryable`, `RequestId` and
`RetryAfterSeconds`. Messages redact bearer tokens and do not expose gateway HTML.

Signed upload URLs are used separately without the API key. Webhook secrets
must stay private; implement receiver signature checks from the API docs.

## Local checks and package build

From the application repository root with Python 3 and .NET SDK 8 or newer installed:

```sh
dotnet restore TranscriptDock.Checks --locked-mode
python3 .ci/check.py
dotnet pack TranscriptDock -c Release -o artifacts
```

The library targets .NET 8; the local check executable also uses .NET 8. Tests call
a loopback fixture, never production. Lockfiles contain no third party packages.
Release workflow: https://github.com/TranscriptDock/transcriptdock-dotnet/actions.
