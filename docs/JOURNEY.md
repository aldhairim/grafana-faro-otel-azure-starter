# Build journey

A step-by-step log of how this starter was built, and the gotchas worth knowing. The goal
was the fastest honest path to **frontend RUM + backend APM + browser→backend tracing +
user correlation** on Grafana Cloud, application-side only.

---

## Step 1 — Backend skeleton with the Grafana OpenTelemetry Distribution

**Goal:** a .NET 10 Azure Function returning static data, instrumented with the Grafana
distro — the lowest-effort way to point .NET OpenTelemetry at Grafana Cloud.

**Choices**
- **.NET 10 isolated worker** (current LTS; Azure Functions v4).
- Telemetry: `Grafana.OpenTelemetry` + `.UseGrafana()`. It reads standard `OTEL_*` env vars
  for the OTLP endpoint/auth, so there's nothing bespoke and **no Application Insights**.
- Prefer the distro over the vanilla SDK because it gets traces + logs + metrics working in
  one call — the team wanted a fast start, not exporter/processor plumbing.

**Setup (essence)**
```csharp
var otel = services.AddOpenTelemetry();
otel.UseGrafana(cfg => cfg.DeploymentEnvironment = "development");
otel.WithTracing(t => t.AddSource("Portfolio").SetSampler(new AlwaysOnSampler()));
otel.WithMetrics(m => m.AddMeter("Portfolio"));
```

**Notes**
- The distro 1.11.0 pulls the whole OpenTelemetry line (SDK 1.17.x) transitively — no other
  OpenTelemetry packages are needed, and `services.AddOpenTelemetry()` is available without a
  separate `OpenTelemetry.Extensions.Hosting` reference.
- `.UseGrafana()` lives on the `IOpenTelemetryBuilder` returned by `AddOpenTelemetry()`; one
  call covers traces, metrics and logs.

---

## Step 2 — A real server span for isolated Functions

**The catch:** on Azure Functions **isolated**, the HTTP trigger does **not** produce a
server span out of the box (there's no ASP.NET Core pipeline for the trigger). So the distro
gives you logs + metrics for free, but a request wouldn't appear as a trace.

**Fix (`Telemetry.cs`):** extract the incoming W3C `traceparent` and start our own
`ActivityKind.Server` span named per route. This is also exactly what stitches the browser
(Faro) trace to the backend.

Two things that matter:
1. **`AlwaysOnSampler`** — the host's ambient activity is non-recorded, and the default
   ParentBased sampler would suppress our span for requests that arrive with no traceparent.
2. **Only adopt the extracted parent if it's valid** — an invalid context makes the span
   non-recording.

Child spans (`valuation.compute`, `pricing.lookup`, `risk.check`) are opened with the same
`ActivitySource`, so traces show real structure, not a single flat span.

---

## Step 3 — Frontend RUM with Faro + trace propagation

React + TypeScript + Vite app instrumented with `@grafana/faro-react`:
- `getWebInstrumentations()` → JS errors, sessions, Web Vitals, page-load performance.
- `TracingInstrumentation` with `propagateTraceHeaderCorsUrls: [API_URL]` → injects the W3C
  `traceparent` on fetches to the API, so **browser → Function is one distributed trace**.
- `ReactIntegration()` for component/render context on errors.
- `faro.api.setUser({ id: "user-1234", ... })` simulates a signed-in user.

The app sends `X-User-Id: <same id>` on every API call; the backend copies it to
`enduser.id` on the span — so the **RUM user and the trace user match**.

**CORS gotcha:** the browser will only *send* `traceparent`/`X-User-Id` cross-origin if the
backend's CORS preflight allows them. Locally, `func` reflects requested headers; when
deployed, add your frontend origin to the Function app's CORS allow-list.

---

## Step 4 — Flows to prove the story

Kept to app-only flows (no external services to provision):
- `GET /portfolio` — the primary read; nested `valuation.compute` span.
- `GET /positions` — a second read with simulated `pricing.lookup` latency.
- `POST /orders` — a write; `risk.check` span + a custom counter `portfolio.orders.placed`.
- `POST /orders?fail=true` — forces a failure so you get an **error span + error log** to
  drill into.

`gen-session.mjs` (Playwright) drives all of this headlessly to populate dashboards.

---

## Gotchas worth knowing

1. **Isolated Functions don't emit a request span** — you must add one (Step 2), plus an
   AlwaysOn sampler.
2. **`deployment.environment`**: the distro sets a default that **overrides**
   `OTEL_RESOURCE_ATTRIBUTES`. Set it via the distro's own config lambda
   (`UseGrafana(cfg => cfg.DeploymentEnvironment = ...)`).
3. **CORS preflight** must allow `traceparent` and `X-User-Id`, or cross-origin propagation
   silently drops them.
4. **`func start` needs `AzureWebJobsStorage`** — run Azurite (or point it at a storage
   account); the example uses `UseDevelopmentStorage=true`.
5. **.NET 10 on Linux** deploys only to the **Flex Consumption** plan (Linux Consumption
   can't host it) — relevant only when you move off localhost.

## When you'd reach for the vanilla OpenTelemetry SDK instead

The distro is the fast path and is enough for the vast majority of setups. Drop to the raw
SDK only if you need fine-grained control the distro doesn't surface — a custom exporter
pipeline, bespoke processors/samplers beyond what's exposed, or trimming the bundled
instrumentation set aggressively. For this PoV, the distro was the right call.

## Out of scope (for now)

Cloud-infrastructure monitoring — Key Vault / SQL / Cosmos dependency spans, API gateway and
WAF/edge telemetry via the Azure Monitor datasource, etc. — is intentionally deferred so the
first pass stays focused on the app-side story and is trivial to run and tear down.
