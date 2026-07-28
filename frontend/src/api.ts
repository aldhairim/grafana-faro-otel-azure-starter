import { getUserId } from './faro';

const API_URL = (import.meta.env.VITE_API_URL as string) || 'http://localhost:7071';

// Small fetch helper. Always sends X-User-Id (for backend user attribution). Faro's
// TracingInstrumentation auto-injects the traceparent header on this request.
export async function callApi(path: string, init: RequestInit = {}): Promise<string> {
  const headers: Record<string, string> = {
    'X-User-Id': getUserId(),
    ...(init.body ? { 'Content-Type': 'application/json' } : {}),
    ...((init.headers as Record<string, string>) || {}),
  };
  const res = await fetch(`${API_URL}/api/${path}`, { ...init, headers });
  const text = await res.text();
  if (!res.ok) throw new Error(`API ${res.status}: ${text}`);
  return text;
}
