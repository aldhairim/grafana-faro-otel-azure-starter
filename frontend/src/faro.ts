import {
  initializeFaro,
  getWebInstrumentations,
  ReactIntegration,
} from '@grafana/faro-react';
import { TracingInstrumentation } from '@grafana/faro-web-tracing';

// A simulated signed-in user. Set once per page load; the app forwards it to the backend
// as X-User-Id so the browser user (Faro setUser) and the backend user (enduser.id on the
// span) match — enabling cross-tier user correlation in Grafana.
let currentUserId = '';
export function getUserId(): string {
  return currentUserId;
}

const REGIONS = ['us-east', 'us-west', 'eu-west', 'ap-south'];

export function initFaro() {
  const url = import.meta.env.VITE_FARO_URL as string | undefined;
  if (!url || url.includes('<')) {
    console.warn('[faro] VITE_FARO_URL not configured — RUM disabled');
    return;
  }

  const apiUrl = import.meta.env.VITE_API_URL as string | undefined;

  const faro = initializeFaro({
    url,
    app: {
      name: (import.meta.env.VITE_FARO_APP_NAME as string) || 'portfolio-web',
      version: '1.0.0',
      environment: (import.meta.env.VITE_ENVIRONMENT as string) || 'development',
    },
    instrumentations: [
      // Web instrumentations: JS errors, sessions, Web Vitals, page load performance.
      ...getWebInstrumentations(),

      // Tracing: injects a W3C `traceparent` on fetches to the API so browser -> backend
      // becomes a single distributed trace. The backend must allow the header via CORS.
      new TracingInstrumentation({
        instrumentationOptions: {
          propagateTraceHeaderCorsUrls: apiUrl ? [new RegExp(apiUrl)] : [],
        },
      }),

      // React integration (component/render context on errors).
      new ReactIntegration(),
    ],
  });

  // Simulate a logged-in user and attach them to the session/telemetry.
  const n = Math.floor(Math.random() * 9000) + 1000;
  currentUserId = `user-${n}`;
  faro.api.setUser({
    id: currentUserId,
    username: `user${n}@example.com`,
    attributes: { region: REGIONS[n % REGIONS.length] },
  });
}
