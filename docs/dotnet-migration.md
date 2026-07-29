# Add the Grafana OpenTelemetry Distribution to an existing .NET 10 Azure Function

A task guide for instrumenting a .NET 10 **isolated-worker** Azure Functions app you already
have. For a working reference, see [`backend/Program.cs`](../backend/Program.cs),
[`backend/Telemetry.cs`](../backend/Telemetry.cs), and the [README](../README.md).

## 1. Install

```bash
dotnet add package Grafana.OpenTelemetry
```

That single package pulls the whole OpenTelemetry line (SDK 1.17.x) transitively — no other
OpenTelemetry packages are needed.

## 2. Wire up the distribution

In `Program.cs`, on the host's services:

```csharp
using Grafana.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Trace;

var otel = services.AddOpenTelemetry();

// One call configures the OTLP exporter + instrumentations for traces, logs AND metrics
// from standard OTEL_* env vars. Set DeploymentEnvironment here — the distro's default
// otherwise overrides OTEL_RESOURCE_ATTRIBUTES.
otel.UseGrafana(cfg => cfg.DeploymentEnvironment = "production");
```

For an **ASP.NET Core** service this is essentially all you need — the distro auto-instruments
incoming requests. On the **isolated Functions worker** there's one extra step (next).

## 3. Add a server span (isolated worker only)

The isolated worker does **not** emit a server span for the HTTP trigger, so create one and
extract the incoming `traceparent` (this is also what stitches a browser/Faro trace to the
backend). Register your `ActivitySource` and use an `AlwaysOnSampler`:

```csharp
otel.WithTracing(t => t
    .AddSource("MyApi")                    // your ActivitySource name
    .SetSampler(new AlwaysOnSampler()));   // host ambient activity is non-recorded
```

`Telemetry.cs`:

```csharp
using System.Diagnostics;
using OpenTelemetry;                       // ActivityContext.IsValid()
using OpenTelemetry.Context.Propagation;

public static class Telemetry
{
    public static readonly ActivitySource Source = new("MyApi");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    public static Activity? StartServerSpan(HttpRequestData req, string operationName)
    {
        var parent = Propagator.Extract(default, req.Headers,
            (h, k) => h.TryGetValues(k, out var v) ? v.ToArray() : Array.Empty<string>());

        var span = parent.ActivityContext.IsValid()
            ? Source.StartActivity(operationName, ActivityKind.Server, parent.ActivityContext)
            : Source.StartActivity(operationName, ActivityKind.Server);

        // User correlation: copy the browser-sent id onto the span.
        if (req.Headers.TryGetValues("X-User-Id", out var uv))
            span?.SetTag("enduser.id", uv.FirstOrDefault());

        return span;
    }
}
```

Wrap each function body:

```csharp
using var span = Telemetry.StartServerSpan(req, "GET /portfolio");
// ...handle the request; add child spans via Telemetry.Source.StartActivity(...) as needed
```

> Prefer zero custom code? Run Functions on the ASP.NET Core integration model
> (`ConfigureFunctionsWebApplication` + `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`)
> and the distro emits the server span automatically — at the cost of the ASP.NET Core shared
> runtime and a different function signature.

## 4. (Optional) Custom metrics

```csharp
otel.WithMetrics(m => m.AddMeter("MyApi"));
// var counter = new Meter("MyApi").CreateCounter<long>("my.custom.metric");
```

## 5. Configuration (`OTEL_*`)

Set these as the Function app's **application settings** (locally, in `local.settings.json`):

```
OTEL_SERVICE_NAME           = my-api
OTEL_EXPORTER_OTLP_PROTOCOL = http/protobuf
OTEL_EXPORTER_OTLP_ENDPOINT = https://otlp-gateway-<zone>.grafana.net/otlp
OTEL_EXPORTER_OTLP_HEADERS  = Authorization=Basic <base64 of "<instanceID>:<token>">
```

Get the OTLP endpoint, instance ID, and a token from Grafana Cloud → **Connections → OTLP**.
Build the header value with:

```bash
printf '%s' '<instanceID>:<token>' | base64
```

If a browser calls the Function directly, add the frontend origin to the Function app's
**CORS** allow-list so `traceparent` and `X-User-Id` are accepted.

## 6. Verify

- **Application Observability** shows the service (RED metrics, operations, service map).
- **Tempo** shows the server span (with child spans) — and the Faro browser span as the root
  when the request came from an instrumented frontend.
- **Loki** shows the app logs, each carrying `trace_id`/`span_id` linking back to the trace.
