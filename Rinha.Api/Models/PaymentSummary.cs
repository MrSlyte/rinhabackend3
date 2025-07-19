namespace Rinha.Api.Models;
public readonly record struct PaymentSummary(
    [property: JsonPropertyName("default")] ProcessorSummary Default,
    [property: JsonPropertyName("fallback")] ProcessorSummary Fallback
);
