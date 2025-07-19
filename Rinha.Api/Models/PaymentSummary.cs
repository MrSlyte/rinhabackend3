namespace Rinha.Api.Models;
internal readonly record struct PaymentSummary(
    [property: JsonPropertyName("default")] ProcessorSummary Default,
    [property: JsonPropertyName("fallback")] ProcessorSummary Fallback
);
