namespace Rinha.Api.Models;

internal readonly record struct PaymentQueueItem(
    PaymentRequest PaymentRequest,
    CancellationToken CancellationToken
);