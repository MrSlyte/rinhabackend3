namespace Rinha.Api.Models;
public readonly record struct PaymentRequest(
[property: JsonPropertyName("correlationId")] Guid CorrelationId,
[property: JsonPropertyName("amount")] decimal Amount
);
