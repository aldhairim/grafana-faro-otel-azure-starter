// Headless browser sessions that generate Faro RUM data (page loads, sessions, Web Vitals,
// user clicks -> API calls with traceparent) and backend traces/logs/metrics. Handy for a
// live demo without clicking by hand.
//
//   npm install                      # once, to get Playwright
//   npx playwright install chromium  # once, to get the browser
//   node gen-session.mjs             # 3 sessions against http://localhost:5173
//   SESSIONS=8 URL=https://my-site node gen-session.mjs
import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5173';
const SESSIONS = Number(process.env.SESSIONS || 3);
const FLOWS = ['View Portfolio', 'View Positions', 'Place Order'];

const browser = await chromium.launch();
for (let s = 0; s < SESSIONS; s++) {
  const ctx = await browser.newContext(); // fresh context = distinct Faro session
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'load' });
  await page.waitForTimeout(1200);

  for (const flow of FLOWS) {
    try { await page.getByRole('button', { name: flow, exact: true }).click({ timeout: 3000 }); }
    catch { /* button may briefly show "Loading…" */ }
    await page.waitForTimeout(900);
  }

  // ~half the sessions hit a frontend error, so RUM has JS errors to drill into.
  if (s % 2 === 0) {
    const errBtns = ['Trigger JS Error', 'Break Widget', 'Unhandled Rejection'];
    const label = errBtns[s % errBtns.length];
    try { await page.getByRole('button', { name: label }).click({ timeout: 3000 }); }
    catch { /* uncaught error from the handler is expected */ }
    await page.waitForTimeout(800);
    console.log(`  session ${s + 1}: triggered "${label}"`);
  }

  await page.waitForTimeout(4000); // let Faro flush
  await ctx.close();               // unload -> Faro sends remaining beacons
  console.log(`session ${s + 1}/${SESSIONS}: exercised ${FLOWS.length} flows`);
}
await browser.close();
console.log('done');
