# Grafana Cloud RUM + APM starter — Faro + OpenTelemetry on Azure Functions

A small, self-contained example that shows **end-to-end observability** for a
browser → serverless-backend app using **Grafana Cloud**, with **no vendor agent** and
**no Application Insights**:

- **Frontend RUM** with **Grafana Faro** — JS errors, sessions, Web Vitals, page performance
- **Backend APM** with the **Grafana OpenTelemetry Distribution for .NET** — traces, logs, metrics
- **Browser → backend distributed tracing** via W3C `traceparent` propagation
- **User correlation** across the browser session and the backend span

This first pass is intentionally **application-only** — no cloud infrastructure to stand up.
Everything runs locally against your Grafana Cloud stack and tears down cleanly.

```
Browser (React + Faro)                         Grafana Cloud
  │  click "View Portfolio"                       ┌───────────────┐
  │  fetch /api/portfolio                         │  Faro          │  ← RUM (errors,
  │    + traceparent  + X-User-Id  ───────────────▶  Collector     │    sessions,
  ▼                                               │                │    Web Vitals)
Azure Functions (.NET 10 isolated)               │  Tempo / Loki  │  ← traces + logs
  .UseGrafana()  →  OTLP  ───────────────────────▶  Mimir         │    + metrics
  server span + child spans, logs, metrics       └───────────────┘
```

Because Faro injects the same `traceparent` the backend continues, a single click shows up
as **one trace** spanning the browser and the Function; because the browser sends
`X-User-Id` (matching Faro's `setUser`), the **same user id** appears on the RUM session and
the backend span.

## What's in here

| Path | What |
|---|---|
| `frontend/` | React + TypeScript + Vite app, Faro-instrumented |
| `backend/` | .NET 10 Azure Functions (isolated worker), Grafana OTel distro |
| `gen-session.mjs` | Playwright traffic generator (RUM + backend load) |
| `docs/JOURNEY.md` | step-by-step build log + the gotchas worth knowing |

### Backend endpoints (all anonymous, app-only — no external dependencies)
| Route | Flow | Telemetry it shows |
|---|---|---|
| `GET /api/portfolio` | View Portfolio | server span + `valuation.compute` child span |
| `GET /api/positions` | View Positions | server span + `pricing.lookup` child span (latency) |
| `POST /api/orders` | Place Order | server span + `risk.check` child span + `portfolio.orders.placed` metric |
| `POST /api/orders?fail=true` | Force a failure | error span + error log (for the "what broke" story) |

## Prerequisites

- **Node 18+**
- **.NET 10 SDK**
- **Azure Functions Core Tools v4** (`func`)
- A **Grafana Cloud** stack (free tier is fine) with **Frontend Observability** available
- For local backend runs: **Azurite** (or any `AzureWebJobsStorage`) for the Functions host

## 1. Get your Grafana Cloud credentials

**Faro (frontend):** Grafana Cloud → **Frontend Observability** → create a web app →
copy the **collector URL** (looks like `https://faro-collector-<zone>.grafana.net/collect/<app-key>`).

**OTLP (backend):** Grafana Cloud → **Connections / OTLP** (or your stack's OTel page) →
note the **OTLP endpoint** and create a token. The backend auth header is
`Authorization=Basic <base64>` where `<base64>` encodes `"<instanceID>:<token>"`:

```bash
printf '%s' '<instanceID>:<token>' | base64
```

## 2. Configure

**Backend** — copy the example and fill in your OTLP values:

```bash
cp backend/local.settings.json.example backend/local.settings.json
# edit OTEL_EXPORTER_OTLP_ENDPOINT and OTEL_EXPORTER_OTLP_HEADERS
```

**Frontend** — copy the example and fill in your Faro URL:

```bash
cp frontend/.env.example frontend/.env
# edit VITE_FARO_URL (and VITE_API_URL if not localhost)
```

> `local.settings.json` and `.env` are git-ignored — never commit real credentials.

## 3. Run locally

```bash
# terminal 1 — backend (http://localhost:7071)
cd backend
func start

# terminal 2 — frontend (http://localhost:5173)
cd frontend
npm install
npm run dev
```

Open http://localhost:5173, sign-in is simulated, click the flow buttons, and try the
error buttons.

## 4. Generate demo traffic (optional)

```bash
npm install                       # root — installs Playwright
npx playwright install chromium
SESSIONS=8 node gen-session.mjs   # drives the local site through every flow
```

## 5. See it in Grafana Cloud

- **Frontend Observability** → your app: sessions, Web Vitals, and the JS errors with stack traces.
- **Explore → Tempo**: search `{ name = "GET /portfolio" }` (or `POST /orders`). Each trace has the
  Faro browser span as the **root** and the Function server + child spans beneath it.
- **Explore → Loki**: the app logs, each carrying `trace_id`/`span_id` that link back to the trace.
- **Explore → Mimir/Prometheus**: `portfolio_orders_placed_total` plus the distro's runtime/HTTP metrics.
- **User correlation**: the browser session's user id (e.g. `user-1234`) matches `enduser.id` on the span.

## Deploying (later / optional)

This starter is meant to run locally, but the backend deploys to Azure Functions like any
isolated .NET app (`func azure functionapp publish <app>`), and the frontend is static
(`npm run build` → any static host). One note if you deploy the backend to Linux: **.NET 10
requires the Flex Consumption plan** (Linux Consumption doesn't support it). Set the same
`OTEL_*` values as application settings, and add your frontend origin to the Function app's
CORS allow-list so the browser may send `traceparent`/`X-User-Id`.

Cloud-infrastructure monitoring (Key Vault, SQL, Cosmos, gateways, WAF, etc.) is deliberately
out of scope for this first pass.

## Why the Grafana distribution (not the vanilla OTel SDK)?

The distro (`Grafana.OpenTelemetry`) is a single `.UseGrafana()` call that wires the OTLP
exporter and a sensible set of instrumentations for traces, metrics **and** logs from
standard `OTEL_*` env vars — a much faster path to a working frontend+backend setup than
hand-assembling exporters and processors. You can still drop to the raw SDK later; see
`docs/JOURNEY.md`.
