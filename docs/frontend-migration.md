# Add Grafana Faro to an existing React app

A task guide for instrumenting a React app you already have. For a working reference, see
[`frontend/src/faro.ts`](../frontend/src/faro.ts) and the [README](../README.md).

## 1. Install

```bash
npm install @grafana/faro-react @grafana/faro-web-tracing
```

## 2. Initialize Faro

Create `src/faro.ts`:

```ts
import { initializeFaro, getWebInstrumentations, ReactIntegration } from '@grafana/faro-react';
import { TracingInstrumentation } from '@grafana/faro-web-tracing';

export function initFaro() {
  const apiUrl = import.meta.env.VITE_API_URL as string; // your backend origin

  initializeFaro({
    url: import.meta.env.VITE_FARO_URL as string,   // Faro collector URL (see step 5)
    app: { name: 'my-web-app', version: '1.0.0', environment: 'production' },
    instrumentations: [
      ...getWebInstrumentations(),          // JS errors, sessions, Web Vitals, page-load perf
      new TracingInstrumentation({          // browser -> backend trace propagation
        instrumentationOptions: {
          propagateTraceHeaderCorsUrls: [new RegExp(apiUrl)],
        },
      }),
      new ReactIntegration(),               // component/render context on errors
    ],
  });
}
```

## 3. Initialize before the app renders

In your entry file (`src/main.tsx`), call `initFaro()` **before** `createRoot(...).render()`
so Faro captures the whole session:

```ts
import { initFaro } from './faro';
initFaro();
// ...then render your app
```

That alone gives you RUM: **JS errors, sessions, Web Vitals, page performance**.

## 4. Trace propagation (browser → backend)

`propagateTraceHeaderCorsUrls` makes Faro inject a W3C `traceparent` header on `fetch`/XHR
calls whose URL matches the pattern. Point it at your API origin(s).

Two requirements for the trace to actually connect:
- The **backend must continue the trace** (accept the incoming `traceparent`) — see
  [`docs/dotnet-migration.md`](dotnet-migration.md) for the .NET Functions side.
- The backend's **CORS** must allow the `traceparent` header, or the browser drops it on
  cross-origin requests. Add your frontend origin to the backend's CORS allow-list.

## 5. (Optional) User correlation

Attach a user so the RUM session and the backend span show the same identity:

```ts
faro.api.setUser({ id: userId, username, attributes: { /* ... */ } });
```

Send the **same id** to your backend on API calls (this sample uses an `X-User-Id` header;
see [`frontend/src/api.ts`](../frontend/src/api.ts)) and have the backend copy it onto the
span (e.g. `enduser.id`).

## 6. Get the collector URL

Grafana Cloud → **Frontend Observability** → create/select a web app → **Web SDK** → copy the
collector URL and set it as `VITE_FARO_URL`:

```
https://faro-collector-<zone>.grafana.net/collect/<app-key>
```

## 7. Verify

- **Frontend Observability** shows sessions, Web Vitals, and JS errors with stack traces.
- In **Tempo**, a user action produces a trace whose **root** is the browser span; if the
  backend is instrumented, its server span appears beneath it in the same trace.
