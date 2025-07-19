namespace Rinha.Api.Models;

internal readonly record struct ProcessedPayment(
    Guid CorrelationId,
    decimal Amount,
    DateTimeOffset ProcessedAt,
    string ProcessorUsed = ""
);