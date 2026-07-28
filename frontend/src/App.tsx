import { useState } from 'react';
import { getUserId } from './faro';
import { callApi } from './api';

// User flows — each hits the backend and produces a browser->backend distributed trace.
const FLOWS = [
  { key: 'portfolio', label: 'View Portfolio', run: () => callApi('portfolio') },
  { key: 'positions', label: 'View Positions', run: () => callApi('positions') },
  { key: 'order',     label: 'Place Order',    run: () => callApi('orders', { method: 'POST', body: JSON.stringify({ ticker: 'FUND-A', quantity: 100 }) }) },
  { key: 'order-fail', label: 'Place Order (force failure)', run: () => callApi('orders?fail=true', { method: 'POST', body: JSON.stringify({ ticker: 'FUND-A', quantity: 999999 }) }) },
];

export function App() {
  const [out, setOut] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run(flow: (typeof FLOWS)[number]) {
    setBusy(flow.key); setError(null); setOut(null);
    try { setOut(await flow.run()); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(null); }
  }

  // --- Frontend error demos (Faro captures these with stack + session context) ---------
  function triggerJsError() { throw new Error('Render failed: valuation service returned null'); }
  function breakWidget() { const w: any = undefined; return w.render(); }
  function unhandledRejection() { Promise.reject(new Error('Async pricing fetch rejected (timeout)')); }

  const btn: React.CSSProperties = { padding: '0.6rem 1.1rem', fontSize: 14, marginRight: 10, marginTop: 8 };

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 720, margin: '3rem auto', padding: '0 1rem' }}>
      <h1>Portfolio</h1>
      <p style={{ color: '#666' }}>Signed in as <strong>{getUserId() || '(RUM disabled)'}</strong></p>

      <section>
        {FLOWS.map((f) => (
          <button key={f.key} onClick={() => run(f)} disabled={busy === f.key} style={btn}>
            {busy === f.key ? 'Loading…' : f.label}
          </button>
        ))}
      </section>

      {error && <p style={{ color: 'crimson' }}>Error: {error}</p>}
      {out && (
        <pre style={{ marginTop: '1rem', background: '#f6f8fa', padding: '1rem', borderRadius: 6, overflowX: 'auto', fontSize: 13 }}>
          {(() => { try { return JSON.stringify(JSON.parse(out), null, 2); } catch { return out; } })()}
        </pre>
      )}

      <hr style={{ margin: '2rem 0', border: 'none', borderTop: '1px solid #ddd' }} />
      <h3>Simulate frontend errors</h3>
      <p style={{ color: '#666', fontSize: 14 }}>These raise real browser errors — Faro captures them with stack trace + session context.</p>
      <button onClick={triggerJsError} style={btn}>Trigger JS Error</button>
      <button onClick={breakWidget} style={btn}>Break Widget</button>
      <button onClick={unhandledRejection} style={btn}>Unhandled Rejection</button>
    </main>
  );
}
