namespace Rinha.Api.Models;
internal readonly record struct HealthResponse(
[property: JsonPropertyName("failing")] bool Failing,
[property: JsonPropertyName("minResponseTime")] int MinResponseTime
);
