namespace Rinha.Api;

public sealed class TimeoutMiddleware(RequestDelegate next, TimeSpan timeout)
{
    private readonly RequestDelegate _next = next;
    private readonly TimeSpan _timeout = timeout;

    public async Task Invoke(HttpContext ctx)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            ctx.RequestAborted,
            new CancellationTokenSource(_timeout).Token);

        try
        {
            ctx.RequestAborted.ThrowIfCancellationRequested();
            await _next(ctx.WithCancellation(cts.Token));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!ctx.Response.HasStarted)
                ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        }
    }
}

