using Rinha.Api.Models;
using StackExchange.Redis;
using System.Net;
using System.Text.Json;

namespace Rinha.Api.Services;
public sealed class PaymentService
{
    private const string RedisKey = "payments";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _redisDb;
    private readonly SemaphoreSlim _processingSemaphore = new(Environment.ProcessorCount * 2);
    private readonly string _urlDefault = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_DEFAULT") ?? "";
    private readonly string _urlFallback = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_FALLBACK") ?? "";

    public PaymentService(IHttpClientFactory httpClientFactory, IConnectionMultiplexer redis)
    {
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _redisDb = _redis.GetDatabase();
    }

    internal async Task ProcessPayment(PaymentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSinglePaymentAsync(request, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task ProcessSinglePaymentAsync(PaymentRequest payment, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("PaymentProcessor");
        var processorRequest = new PaymentProcessorRequest(
            payment.CorrelationId,
            payment.Amount,
            DateTimeOffset.UtcNow
        );

        var healthMonitor = HealthMonitor.Instance;
        var useDefault = healthMonitor.ShouldUseDefault();

        var baseUrl = useDefault
            ? _urlDefault
            : _urlFallback;

        var maxRetries = 3;
        var retryDelay = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            try
            {
                var response = await client.PostAsJsonAsync($"{baseUrl}/payments", processorRequest, AppJsonSerializerContext.Default.PaymentProcessorRequest, cancellationToken);

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    break;
                }
                response.EnsureSuccessStatusCode();

                if (response.IsSuccessStatusCode)
                {
                    var processedPayment = new ProcessedPayment
                    {
                        CorrelationId = payment.CorrelationId,
                        Amount = payment.Amount,
                        ProcessedAt = DateTimeOffset.UtcNow,
                        ProcessorUsed = useDefault ? "default" : "fallback"
                    };

                    // Salva no Redis (usando SortedSet para facilitar range por data)
                    var score = processedPayment.ProcessedAt.ToUnixTimeMilliseconds();
                    var value = JsonSerializer.Serialize(processedPayment, AppJsonSerializerContext.Default.ProcessedPayment);
                    await _redisDb.SortedSetAddAsync(RedisKey, value, score);
                    break; // Sucesso
                }
                else if (response.StatusCode >= HttpStatusCode.InternalServerError)
                {
                    // Erro 5xx, tenta o outro processor
                    if (useDefault)
                    {
                        healthMonitor.ReportFailure(true);
                        useDefault = false;
                        baseUrl = _urlFallback;
                    }
                    else
                    {
                        healthMonitor.ReportFailure(false);
                        useDefault = true;
                        baseUrl = _urlDefault;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // aplicação está parando; apenas saia
                break;
            }
            catch (HttpRequestException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                // Timeout ou erro de rede, tenta o outro processor
                if (useDefault)
                {
                    healthMonitor.ReportFailure(true);
                    useDefault = false;
                    baseUrl = _urlFallback;
                }
                else
                {
                    healthMonitor.ReportFailure(false);
                    useDefault = true;
                    baseUrl = _urlDefault;
                }
            }
            catch (TaskCanceledException)
            {
                // Timeout
                healthMonitor.ReportSlowness(useDefault);
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (attempt < maxRetries - 1)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay *= 2;
            }
        }
    }

    internal async Task<PaymentSummary> GetPaymentsSummary(DateTimeOffset from, DateTimeOffset to)
    {
        var fromScore = from.ToUnixTimeMilliseconds();
        var toScore = to.ToUnixTimeMilliseconds();

        var redisPayments = await _redisDb.SortedSetRangeByScoreAsync(RedisKey, fromScore, toScore);
        var payments = redisPayments
            .Select(x => JsonSerializer.Deserialize(x!, AppJsonSerializerContext.Default.ProcessedPayment))
            .ToList();

        var defaultSummary = payments
            .Where(p => p.ProcessorUsed == "default")
            .Aggregate(
                new { Count = 0, Amount = 0m },
                (acc, p) => new { Count = acc.Count + 1, Amount = acc.Amount + p.Amount }
            );

        var fallbackSummary = payments
            .Where(p => p.ProcessorUsed == "fallback")
            .Aggregate(
                new { Count = 0, Amount = 0m },
                (acc, p) => new { Count = acc.Count + 1, Amount = acc.Amount + p.Amount }
            );

        return new PaymentSummary(
            new ProcessorSummary(defaultSummary.Count, defaultSummary.Amount),
            new ProcessorSummary(fallbackSummary.Count, fallbackSummary.Amount)
        );
    }
}