namespace Rinha.Api.Models;
internal readonly record struct PaymentRequest(
[property: JsonPropertyName("correlationId")] Guid CorrelationId,
[property: JsonPropertyName("amount")] decimal Amount
);
