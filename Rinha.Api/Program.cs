using Microsoft.AspNetCore.Http.Json;
using Rinha.Api.Models;
using Rinha.Api.Services;
using StackExchange.Redis;
using System.Text.Json;

namespace Rinha.Api;

public static class Program
{
    public static void Main(string[] args)
    {
        ThreadPool.SetMinThreads(300, 300);
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(2);  // fecha conexões ociosas
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(2);
            o.Limits.MaxRequestBodySize = 64 * 1024;                // opcional
        });

        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        builder.Services.AddHttpClient("PaymentProcessor", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            MaxConnectionsPerServer = 100,
            UseCookies = false
        });

        var redisEndpoint = Environment.GetEnvironmentVariable("REDIS_ENDPOINT")
                ?? "redis:6379,abortConnect=false";
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(redisEndpoint));

        builder.Services.AddSingleton<PaymentService>();
        builder.Services.AddSingleton<HealthMonitor>();


        var app = builder.Build();

        // Configure the HTTP request pipeline.

        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("Server", "rinha");
            await next();
        });

        app.UseMiddleware<TimeoutMiddleware>(TimeSpan.FromSeconds(2));

        var paymentService = app.Services.GetRequiredService<PaymentService>();
        var healthMonitor = app.Services.GetRequiredService<HealthMonitor>();

        // Inicializar monitoramento de saúde
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await healthMonitor.CheckHealthAsync();
                await Task.Delay(6000); // Respeitando limite de 1 call/5s + buffer
            }
        });

        // POST /payments
        app.MapPost("/payments", async (PaymentRequest request, HttpContext context) =>
        {
            try
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                await paymentService.ProcessPayment(request, context.RequestAborted);
                return Results.Accepted();
            }
            catch (Exception)
            {
                return Results.InternalServerError();
            }
        });

        // GET /payments-summary
        app.MapGet("/payments-summary", async (HttpContext context, string? from, string? to) =>
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var fromDate = string.IsNullOrEmpty(from) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(from);
            var toDate = string.IsNullOrEmpty(to) ? DateTimeOffset.MaxValue : DateTimeOffset.Parse(to);

            var summary = await paymentService.GetPaymentsSummary(fromDate, toDate);
            return Results.Ok(summary);
        });

        app.Run();
    }
}

[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentRequest[]))]
[JsonSerializable(typeof(PaymentProcessorRequest))]
[JsonSerializable(typeof(PaymentProcessorRequest[]))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(HealthResponse[]))]
[JsonSerializable(typeof(PaymentSummary))]
[JsonSerializable(typeof(PaymentSummary[]))]
[JsonSerializable(typeof(ProcessedPayment))]
[JsonSerializable(typeof(ProcessedPayment[]))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}
