namespace Rinha.Api.Models;
public readonly record struct ProcessorSummary(
[property: JsonPropertyName("totalRequests")] int TotalRequests,
[property: JsonPropertyName("totalAmount")] decimal TotalAmount
);
