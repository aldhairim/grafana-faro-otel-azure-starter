# Grafana Cloud RUM + APM starter — Faro + OpenTelemetry on Azure Functions

A small, self-contained example that shows **end-to-end observability** for a
browser → Azure Functions app using **Grafana Cloud**, with **no vendor agent**:

- **Frontend RUM** with **Grafana Faro** — JS errors, sessions, Web Vitals, page performance
- **Backend APM** with the **Grafana OpenTelemetry Distribution for .NET** — traces, logs, metrics
- **Browser → backend distributed tracing** via W3C `traceparent` propagation
- **User correlation** across the browser session and the backend span

This README focuses on **how the RUM and APM are instrumented** and **how to get the
Grafana Cloud credentials**.

![Architecture: a browser (React + Faro) sends RUM directly to the Grafana Cloud Faro Collector and calls an Azure Functions (.NET 10 isolated) backend with a traceparent and X-User-Id header; the backend uses the Grafana OpenTelemetry Distribution (.UseGrafana()) to send traces, logs and metrics directly to Tempo/Loki/Mimir over OTLP. The shared traceparent makes one distributed trace across browser and backend, and the shared user id ties Faro's setUser to the span's enduser.id.](docs/architecture.png)

Because Faro injects the same `traceparent` the backend continues, a single click shows up
as **one trace** spanning the browser and the Function; because the browser sends
`X-User-Id` (matching Faro's `setUser`), the **same user id** appears on the RUM session and
the backend span.

## What's in here

| Path | What |
|---|---|
| `frontend/` | React + TypeScript + Vite app, Faro-instrumented |
| `backend/` | .NET 10 Azure Functions (isolated worker), Grafana OTel distro |
| `gen-session.mjs` | Playwright script that drives the flows to populate telemetry |
| `docs/JOURNEY.md` | build log + the gotchas worth knowing |
| `docs/frontend-migration.md` | how to add Faro to an **existing** React app |
| `docs/dotnet-migration.md` | how to add the Grafana OTel distro to an **existing** .NET 10 Function |

### Backend endpoints
| Route | Flow | Telemetry it shows |
|---|---|---|
| `GET /api/portfolio` | View Portfolio | server span + `valuation.compute` child span |
| `GET /api/positions` | View Positions | server span + `pricing.lookup` child span (latency) |
| `POST /api/orders` | Place Order | server span + `risk.check` child span + `portfolio.orders.placed` metric |
| `POST /api/orders?fail=true` | Force a failure | error span + error log (for the "what broke" story) |

---

## How the frontend RUM is instrumented (Faro)

All of it lives in [`frontend/src/faro.ts`](frontend/src/faro.ts). One `initializeFaro` call
wires the whole RUM story:

```ts
const faro = initializeFaro({
  url,                                   // your Faro collector URL (see credentials below)
  app: { name: 'portfolio-web', version: '1.0.0', environment: 'development' },
  instrumentations: [
    ...getWebInstrumentations(),         // JS errors, sessions, Web Vitals, page-load perf
    new TracingInstrumentation({         // injects W3C traceparent on API fetches
      instrumentationOptions: {
        propagateTraceHeaderCorsUrls: [new RegExp(apiUrl)],
      },
    }),
    new ReactIntegration(),              // component/render context on errors
  ],
});

// Simulated signed-in user -> attached to every session and event
faro.api.setUser({ id: 'user-1234', username: 'user1234@example.com', attributes: { region: 'us-east' } });
```

- **`getWebInstrumentations()`** is what captures errors, sessions, and Web Vitals — no extra code.
- **`TracingInstrumentation` + `propagateTraceHeaderCorsUrls`** is what makes browser→backend
  tracing work: it adds the `traceparent` header to fetches whose URL matches the API.
- **`setUser`** is the browser half of user correlation. The fetch helper
  ([`frontend/src/api.ts`](frontend/src/api.ts)) sends the same id as `X-User-Id` on every call.

Faro is initialized **before** the app renders (see `frontend/src/main.tsx`) so it captures
the full session.

## How the backend APM is instrumented (Grafana OTel distribution)

Two files: [`backend/Program.cs`](backend/Program.cs) (wiring) and
[`backend/Telemetry.cs`](backend/Telemetry.cs) (the span + metric).

**Wiring — one `.UseGrafana()` covers traces, logs and metrics:**
```csharp
var otel = services.AddOpenTelemetry();
otel.UseGrafana(cfg => cfg.DeploymentEnvironment = "development"); // exporter + instrumentations + logs
otel.WithTracing(t => t
    .AddSource(Telemetry.ActivitySourceName)   // our server + child spans
    .SetSampler(new AlwaysOnSampler()));       // see note below
otel.WithMetrics(m => m.AddMeter(Telemetry.MeterName));            // custom app metrics
```

The distro reads standard `OTEL_*` settings for the endpoint/auth, so there is no exporter
plumbing to write.

**The one thing isolated Functions need — a manual server span.** The isolated worker does
**not** emit a request span for the HTTP trigger, so `Telemetry.cs` extracts the incoming
`traceparent` and starts one itself. This is also what the browser trace attaches to:
```csharp
var parent = Propagator.Extract(default, req.Headers, (h, k) => h.TryGetValues(k, out var v) ? v.ToArray() : []);
using var span = parent.ActivityContext.IsValid()
    ? Source.StartActivity(name, ActivityKind.Server, parent.ActivityContext)
    : Source.StartActivity(name, ActivityKind.Server);

// backend half of user correlation
var userId = req.Headers.TryGetValues("X-User-Id", out var uv) ? uv.FirstOrDefault() : null;
if (!string.IsNullOrEmpty(userId)) span?.SetTag("enduser.id", userId);
```
`AlwaysOnSampler` is required: the host's ambient activity is non-recorded, so the default
ParentBased sampler would drop our span for requests that arrive without a `traceparent`.

> This ~30-line span is the *only* non-standard code in the project, and it exists solely
> because the isolated worker doesn't auto-instrument the HTTP trigger. If you run Functions
> on the ASP.NET Core integration model instead, the distro's ASP.NET Core instrumentation
> emits the server span for you and this shim goes away — at the cost of the ASP.NET Core
> shared runtime and a different Function signature. We keep the plain isolated model here
> because it's the default for new .NET Functions and keeps the dependency surface minimal.

Child spans (`valuation.compute`, `pricing.lookup`, `risk.check`) and a custom counter
(`portfolio.orders.placed`) use the same source/meter, so traces show real structure and
metrics carry app-level signal.

## Browser → backend tracing & user correlation

- **Trace:** Faro adds `traceparent` on the fetch → `Telemetry.Handle` adopts it → the
  browser span and the Function span (plus child spans) land in **one trace**.
- **User:** Faro `setUser(user-1234)` → sent as `X-User-Id` → copied to `enduser.id` on the
  span, so the RUM session and the backend trace show the **same identity**.
- **CORS:** the browser only *sends* `traceparent`/`X-User-Id` cross-origin if the Function
  app allows them. Add your frontend origin to the Function app's **CORS** allow-list.

## Applying this to your own app

The same wiring drops into an app you already have — step-by-step task guides:
- **[docs/frontend-migration.md](docs/frontend-migration.md)** — add Faro to an existing React app.
- **[docs/dotnet-migration.md](docs/dotnet-migration.md)** — add the Grafana OTel distro to an existing .NET 10 Function.

---

## Getting your Grafana Cloud credentials

### Faro collector URL (frontend RUM)
Grafana Cloud → **Frontend Observability** → create/select a web app → **Web SDK**. Copy the
**collector URL**:
```
https://faro-collector-<zone>.grafana.net/collect/<app-key>
```
Set it as the frontend build variable `VITE_FARO_URL` (see `frontend/.env.example`).

### OTLP endpoint + token (backend APM)
Grafana Cloud → **Connections → OTLP / OpenTelemetry** (or your stack's OTel page). You need:
- the **OTLP endpoint** — `https://otlp-gateway-<zone>.grafana.net/otlp`
- your **instance ID** (a number) and an **API token** (create one with metrics/logs/traces write scope)

The backend authenticates with an HTTP Basic header whose value is
`base64("<instanceID>:<token>")`:
```bash
printf '%s' '<instanceID>:<token>' | base64
```
Set these as the Function app's **application settings** (the keys are listed in
`backend/local.settings.json.example`):
```
OTEL_EXPORTER_OTLP_ENDPOINT = https://otlp-gateway-<zone>.grafana.net/otlp
OTEL_EXPORTER_OTLP_PROTOCOL = http/protobuf
OTEL_EXPORTER_OTLP_HEADERS  = Authorization=Basic <base64 from above>
OTEL_SERVICE_NAME           = portfolio-api
```

> Keep tokens out of source control — `.env` and `local.settings.json` are git-ignored; only
> the `*.example` templates are committed.

---

## See it in Grafana Cloud

- **Frontend Observability** → your app: sessions, Web Vitals, and JS errors with stack traces.
- **Application Observability** → `portfolio-api`: RED metrics (rate/errors/duration), operations,
  and the service map — populated automatically from the OTLP traces/metrics the distro sends.
- **Explore → Tempo**: search `{ name = "GET /portfolio" }` (or `POST /orders`). Each trace has the
  Faro browser span as the **root** and the Function server + child spans beneath it.
- **Explore → Loki**: the app logs, each carrying `trace_id`/`span_id` that link back to the trace.
- **Explore → Mimir/Prometheus**: `portfolio_orders_placed_total` plus the distro's runtime/HTTP metrics.
- **User correlation**: the browser session's user id (e.g. `user-1234`) matches `enduser.id` on the span.

## Why the Grafana distribution (not the vanilla OTel SDK)?

The distro (`Grafana.OpenTelemetry`) is a single `.UseGrafana()` call that wires the OTLP
exporter and a sensible set of instrumentations for traces, metrics **and** logs from
standard `OTEL_*` env vars — a much faster path to a working frontend+backend setup than
hand-assembling exporters and processors. You can still drop to the raw SDK later; see
[`docs/JOURNEY.md`](docs/JOURNEY.md).
