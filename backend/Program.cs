using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Grafana.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PortfolioApi;

// Grafana OpenTelemetry Distribution for .NET.
// One .UseGrafana() call configures the OTLP exporter + instrumentations for traces,
// metrics AND logs, all from standard OTEL_* environment variables (see
// local.settings.json.example). Telemetry goes straight to Grafana Cloud — no vendor agent.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        var otel = services.AddOpenTelemetry();

        // The distro sets DeploymentEnvironment to a default that overrides
        // OTEL_RESOURCE_ATTRIBUTES, so set it explicitly here.
        otel.UseGrafana(cfg =>
            cfg.DeploymentEnvironment = Environment.GetEnvironmentVariable("DEPLOYMENT_ENVIRONMENT") ?? "development");

        // Azure Functions *isolated* doesn't emit a request span for the HTTP trigger, so
        // we create our own server span (Telemetry.cs). AlwaysOnSampler is required: the
        // host's ambient activity is non-recorded, and the default ParentBased sampler
        // would otherwise drop our span for requests that arrive without a traceparent.
        otel.WithTracing(t => t
            .AddSource(Telemetry.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler()));

        // Register our custom Meter so app metrics (e.g. orders placed) export alongside
        // the runtime/HTTP metrics the distro already provides.
        otel.WithMetrics(m => m.AddMeter(Telemetry.MeterName));
    })
    .Build();

host.Run();
