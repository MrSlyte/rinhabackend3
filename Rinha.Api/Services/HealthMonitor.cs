namespace Rinha.Api.Services;
public sealed class HealthMonitor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private volatile bool _defaultFailing = false;
    private volatile bool _fallbackFailing = false;
    private volatile int _defaultMinResponseTime = 0;
    private volatile int _fallbackMinResponseTime = 0;
    private DateTimeOffset _lastHealthCheck = DateTimeOffset.MinValue;

    internal static HealthMonitor Instance { get; private set; } = null!;

    public HealthMonitor(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        Instance = this;
    }

    internal async Task CheckHealthAsync()
    {
        if (DateTimeOffset.UtcNow - _lastHealthCheck < TimeSpan.FromSeconds(5))
            return;

        _lastHealthCheck = DateTimeOffset.UtcNow;

        var client = _httpClientFactory.CreateClient("PaymentProcessor");

        var tasks = new[]
        {
            CheckServiceHealthAsync(client, "http://payment-processor-default:8080", true),
            CheckServiceHealthAsync(client, "http://payment-processor-fallback:8080", false)
        };

        await Task.WhenAll(tasks);
    }

    private async Task CheckServiceHealthAsync(HttpClient client, string baseUrl, bool isDefault)
    {
        try
        {
            var response = await client.GetAsync($"{baseUrl}/payments/service-health");

            if (response.IsSuccessStatusCode)
            {
                var healthData = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.HealthResponse);

                if (isDefault)
                {
                    _defaultFailing = healthData.Failing;
                    _defaultMinResponseTime = healthData.MinResponseTime;
                }
                else
                {
                    _fallbackFailing = healthData.Failing;
                    _fallbackMinResponseTime = healthData.MinResponseTime;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao verificar saúde do serviço {baseUrl}: {ex.Message}");
            if (isDefault)
                _defaultFailing = true;
            else
                _fallbackFailing = true;
        }
    }

    internal bool ShouldUseDefault()
    {
        if (!_defaultFailing)
            return true;

        if (_fallbackFailing)
            return true;

        return false;
    }

    internal void ReportFailure(bool isDefault)
    {
        if (isDefault)
            _defaultFailing = true;
        else
            _fallbackFailing = true;
    }

    internal void ReportSlowness(bool isDefault)
    {
        if (isDefault)
            _defaultMinResponseTime = Math.Max(_defaultMinResponseTime, 1000);
        else
            _fallbackMinResponseTime = Math.Max(_fallbackMinResponseTime, 1000);
    }
}