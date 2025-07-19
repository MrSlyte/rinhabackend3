using Rinha.Api.Models;
using StackExchange.Redis;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace Rinha.Api.Services;
public sealed class PaymentService : IDisposable
{
    private const string RedisKey = "payments";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _redisDb;
    private readonly string _urlDefault = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_DEFAULT") ?? "";
    private readonly string _urlFallback = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_FALLBACK") ?? "";
    
    // Channel para fila de pagamentos
    private readonly Channel<PaymentQueueItem> _paymentQueue;
    private readonly ChannelWriter<PaymentQueueItem> _queueWriter;
    private readonly ChannelReader<PaymentQueueItem> _queueReader;
    
    // Background workers
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly int _workerCount;

    public PaymentService(IHttpClientFactory httpClientFactory, IConnectionMultiplexer redis)
    {
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _redisDb = _redis.GetDatabase();
        
        // Configura o número de workers baseado no número de processadores
        _workerCount = Environment.ProcessorCount;
        
        // Cria o channel com capacidade limitada para controle de backpressure
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        
        _paymentQueue = Channel.CreateBounded<PaymentQueueItem>(options);
        _queueWriter = _paymentQueue.Writer;
        _queueReader = _paymentQueue.Reader;
        
        // Inicia os workers
        _workers = new Task[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            _workers[i] = ProcessPaymentsAsync(_cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Enfileira um pagamento para processamento assíncrono
    /// </summary>
    public async ValueTask<bool> EnqueuePaymentAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested)
            return false;

        var queueItem = new PaymentQueueItem(request, cancellationToken);
        
        try
        {
            await _queueWriter.WriteAsync(queueItem, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryRegisterCorrelationAsync(Guid correlationId)
    {
        // Mantém o registro por 2 h — tempo mais que suficiente pro teste
        var key = $"paid:{correlationId}";
        return await _redisDb.StringSetAsync(
            key,                     // chave
            "1",                     // valor irrelevante
            TimeSpan.FromHours(2),   // TTL
            When.NotExists);         // NX  ==> SETNX
    }

    /// <summary>
    /// Worker que processa pagamentos da fila
    /// </summary>
    private async Task ProcessPaymentsAsync(CancellationToken cancellationToken)
    {
        await foreach (var queueItem in _queueReader.ReadAllAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested || queueItem.CancellationToken.IsCancellationRequested)
                continue;

            try
            {
                await ProcessSinglePaymentAsync(queueItem.PaymentRequest, queueItem.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Ignora cancelamentos
            }
            catch (Exception ex)
            {
                // Log do erro - você pode injetar ILogger aqui
                Console.WriteLine($"Erro ao processar pagamento {queueItem.PaymentRequest.CorrelationId}: {ex.Message}");
            }
        }
    }

    private async Task ProcessSinglePaymentAsync(PaymentRequest payment, CancellationToken cancellationToken)
    {
        if (!await TryRegisterCorrelationAsync(payment.CorrelationId))
        {
            // Já processado em outra tentativa / instância
            return;
        }

        var client = _httpClientFactory.CreateClient("PaymentProcessor");
        var processorRequest = new PaymentProcessorRequest(
            payment.CorrelationId,
            payment.Amount,
            DateTimeOffset.UtcNow
        );

        var healthMonitor = HealthMonitor.Instance;
        var useDefault = healthMonitor.ShouldUseDefault();

        var baseUrl = useDefault ? _urlDefault : _urlFallback;
        var maxRetries = 3;
        var retryDelay = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var response = await client.PostAsJsonAsync(
                    $"{baseUrl}/payments", 
                    processorRequest, 
                    AppJsonSerializerContext.Default.PaymentProcessorRequest, 
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    break; // Erro de validação, não tenta novamente
                }

                if (response.IsSuccessStatusCode)
                {
                    var processedPayment = new ProcessedPayment
                    {
                        CorrelationId = payment.CorrelationId,
                        Amount = payment.Amount,
                        ProcessedAt = DateTimeOffset.UtcNow,
                        ProcessorUsed = useDefault ? "default" : "fallback"
                    };

                    // Salva no Redis usando SortedSet
                    var score = processedPayment.ProcessedAt.ToUnixTimeMilliseconds();
                    var value = JsonSerializer.Serialize(processedPayment, AppJsonSerializerContext.Default.ProcessedPayment);

                    await _redisDb.SortedSetAddAsync(RedisKey, value, score);
                    
                    break; // Sucesso
                }
                else if (response.StatusCode >= HttpStatusCode.InternalServerError)
                {
                    // Erro 5xx, troca para o outro processor
                    SwitchProcessor(ref useDefault, ref baseUrl, healthMonitor);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Timeout ou erro de rede, troca processor
                SwitchProcessor(ref useDefault, ref baseUrl, healthMonitor);
            }
            catch (TaskCanceledException)
            {
                // Timeout
                healthMonitor.ReportSlowness(useDefault);
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            // Delay entre tentativas com backoff exponencial
            if (attempt < maxRetries - 1)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay *= 2;
            }
        }
    }

    private void SwitchProcessor(ref bool useDefault, ref string baseUrl, HealthMonitor healthMonitor)
    {
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

    public async Task<PaymentSummary> GetPaymentsSummary(DateTimeOffset from, DateTimeOffset to)
    {
        var fromScore = from.ToUnixTimeMilliseconds();
        var toScore = to.ToUnixTimeMilliseconds();

        var redisPayments = await _redisDb.SortedSetRangeByScoreAsync(RedisKey, fromScore, toScore);
        var payments = redisPayments
            .Select(x => JsonSerializer.Deserialize(x!, AppJsonSerializerContext.Default.ProcessedPayment))
            .ToList();

        var defaultSummary = payments
            .Where(p => p!.ProcessorUsed == "default")
            .Aggregate(
                new { Count = 0, Amount = 0m },
                (acc, p) => new { Count = acc.Count + 1, Amount = acc.Amount + p!.Amount }
            );

        var fallbackSummary = payments
            .Where(p => p!.ProcessorUsed == "fallback")
            .Aggregate(
                new { Count = 0, Amount = 0m },
                (acc, p) => new { Count = acc.Count + 1, Amount = acc.Amount + p!.Amount }
            );

        return new PaymentSummary(
            new ProcessorSummary(defaultSummary.Count, defaultSummary.Amount),
            new ProcessorSummary(fallbackSummary.Count, fallbackSummary.Amount)
        );
    }

    /// <summary>
    /// Para o processamento gracefully
    /// </summary>
    public async Task StopAsync(TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        // Para de aceitar novos itens
        _queueWriter.Complete();

        // Aguarda workers terminarem ou timeout
        using var cts = new CancellationTokenSource(timeout);
        await _cancellationTokenSource.CancelAsync();

        try
        {
            await Task.WhenAll(_workers).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Timeout ao parar workers de pagamento");
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _queueWriter.Complete();
        
        try
        {
            Task.WaitAll(_workers, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao finalizar workers: {ex.Message}");
        }
        
        _cancellationTokenSource.Dispose();
    }
}