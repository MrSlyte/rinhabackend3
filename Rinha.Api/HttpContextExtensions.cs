namespace Rinha.Api;
internal static class HttpContextExtensions
{
    private const string LinkedTokenKey = "__LinkedCancellationToken";

    internal static HttpContext WithCancellation(this HttpContext context, CancellationToken linkedToken)
    {
        context.Items[LinkedTokenKey] = linkedToken;
        return context;
    }

    internal static CancellationToken GetCancellation(this HttpContext context)
    {
        if (context.Items.TryGetValue(LinkedTokenKey, out var value) &&
            value is CancellationToken token)
        {
            return token;
        }

        return context.RequestAborted;
    }
}