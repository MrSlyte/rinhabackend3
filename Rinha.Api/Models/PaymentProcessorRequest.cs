namespace Rinha.Api.Models;
internal readonly record struct PaymentProcessorRequest(
[property: JsonPropertyName("correlationId")] Guid CorrelationId,
[property: JsonPropertyName("amount")] decimal Amount,
[property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt
);
