using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;   // ActivityContext.IsValid()

namespace PortfolioApi;

// The small amount of glue that Azure Functions *isolated* needs on top of the distro:
// a real server span per request (extracting the incoming W3C traceparent so the browser
// -> backend trace stitches together), plus a custom Meter for app metrics.
public static class Telemetry
{
    public const string ActivitySourceName = "Portfolio";
    public const string MeterName = "Portfolio";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> OrdersPlaced =
        Meter.CreateCounter<long>("portfolio.orders.placed", unit: "{order}", description: "Orders placed");

    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    public static async Task<HttpResponseData> Handle(
        HttpRequestData req, ILogger log, string operationName,
        Func<Activity?, Task<HttpResponseData>> work)
    {
        // Honor the incoming traceparent only if valid (an invalid context makes the span
        // non-recording); otherwise start a root span.
        var parent = Propagator.Extract(default, req.Headers, static (headers, key) =>
            headers.TryGetValues(key, out var v) ? v.ToArray() : Array.Empty<string>());

        using var span = parent.ActivityContext.IsValid()
            ? Source.StartActivity(operationName, ActivityKind.Server, parent.ActivityContext)
            : Source.StartActivity(operationName, ActivityKind.Server);

        span?.SetTag("http.request.method", req.Method);
        span?.SetTag("url.path", req.Url.AbsolutePath);

        // Match the browser user (Faro setUser) to the backend user via X-User-Id, so the
        // same identity appears on the frontend RUM session and the backend span.
        var userId = req.Headers.TryGetValues("X-User-Id", out var uv) ? uv.FirstOrDefault() : null;
        if (!string.IsNullOrEmpty(userId)) span?.SetTag("enduser.id", userId);

        try
        {
            return await work(span);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            span?.AddException(ex);
            log.LogError(ex, "{Operation} failed", operationName);
            var res = req.CreateResponse(HttpStatusCode.InternalServerError);
            await res.WriteAsJsonAsync(new { error = ex.Message });
            return res;
        }
    }
}
