namespace Mathom.Web.Capture;

public record CaptureRequest(string Text, string IdempotencyKey, string? Target = null, DateTimeOffset? CapturedAt = null);
