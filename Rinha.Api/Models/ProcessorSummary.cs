namespace Rinha.Api.Models;
internal readonly record struct ProcessorSummary(
[property: JsonPropertyName("totalRequests")] int TotalRequests,
[property: JsonPropertyName("totalAmount")] decimal TotalAmount
);
