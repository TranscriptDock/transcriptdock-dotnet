using System.Text.Json;

namespace TranscriptDock;

public sealed class ApiException(string code, string message, int? status = null,
    bool retryable = false, string? requestId = null, int? retryAfterSeconds = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? Status { get; } = status;
    public bool Retryable { get; } = retryable;
    public string? RequestId { get; } = requestId;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class JobTimeoutException(JsonElement? job) : TimeoutException("The job is still running. Check it again using its ID.")
{
    public JsonElement? Job { get; } = job;
}
