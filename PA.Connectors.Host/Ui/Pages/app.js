'use strict';
// POSitive Anywhere — connector pages (S5). Served by the connector host, reached ONLY through the
// POSitive Anywhere server at /connectors/ (its portal login; viewing needs Operations Dashboard,
// changes need Operations Settings — the server and the host both check). Same origin as /ops, so
// the connect wizard creates the connector's POSitive API key with the signed-in user's own session
// (/ui/api-keys) — only when the user clicks, and the Developer-ID-sharing confirm still applies.

// Escapes both quote characters too: values land inside double-quoted attributes.
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
const $ = id => document.getElementById(id);
const enc = encodeURIComponent;
const API = '/connectors/api';
let ME = { user: '', canWrite: false };
let timers = [];

// ---- theme --------------------------------------------------------------------------------
(() => {
  try { const t = localStorage.getItem('pa.theme'); if (t) document.documentElement.dataset.theme = t; } catch (e) { /* storage blocked */ }
  $('theme').addEventListener('click', () => {
    const r = document.documentElement;
    r.dataset.theme = r.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem('pa.theme', r.dataset.theme); } catch (e) { /* storage blocked */ }
  });
})();

// ---- calls --------------------------------------------------------------------------------
class ApiError extends Error {
  constructor(message, status, body) { super(message); this.status = status; this.body = body || {}; }
}

async function call(url, opts = {}) {
  const headers = { Accept: 'application/json', ...(opts.body ? { 'Content-Type': 'application/json' } : {}), ...(opts.headers || {}) };
  let res;
  try {
    res = await fetch(url, { ...opts, headers, credentials: 'same-origin', cache: 'no-store' });
  } catch (e) {
    throw new ApiError('The server could not be reached (' + e.message + ').', 0, {});
  }
  const body = res.status === 204 ? {} : await res.json().catch(() => ({}));
  if (res.ok) return body;
  if (res.status === 401 && !body.errorCode) {
    location.href = '/welcome?returnUrl=' + enc('/connectors/' + location.hash);
    throw new ApiError('Your session expired — sign in again.', 401, body);
  }
  let message;
  if (body.errorCode === 'connector_host_unavailable') {
    message = 'The connector service (POSitiveAnywhereConnectors) is not running on the server. Start it in Windows Services, then reload.';
  } else if (res.status === 403 && !body.errorCode) {
    message = (opts.method || 'GET') === 'GET'
      ? 'Your POSitive user cannot see this. Ask whoever manages POSitive security for "Able To Access Operations Dashboard".'
      : 'Changing connectors needs the POSitive permission "Able To Access Operations Settings" (MA 106).';
  } else {
    message = body.detail || body.message || body.title || ('HTTP ' + res.status);
  }
  if (body.errors) message += ' — ' + Object.entries(body.errors).map(([f, m]) => f + ': ' + m.join(' ')).join(' · ');
  throw new ApiError(message, res.status, body);
}
const api = (path, opts) => call(API + path, opts);
const post = (path, data) => api(path, { method: 'POST', body: JSON.stringify(data || {}) });
const put = (path, data) => api(path, { method: 'PUT', body: JSON.stringify(data || {}) });

// ---- little helpers -------------------------------------------------------------------------
const when = iso => iso ? new Date(iso).toLocaleString() : '—';
function ago(iso) {
  if (!iso) return 'never';
  const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return s + ' s ago';
  if (s < 3600) return Math.round(s / 60) + ' min ago';
  if (s < 86400) return Math.round(s / 3600) + ' h ago';
  return Math.round(s / 86400) + ' d ago';
}
const agoHtml = iso => '<span title="' + esc(when(iso)) + '">' + esc(ago(iso)) + '</span>';
const banner = (msg, info) => {
  const b = $('banner');
  if (!msg) { b.hidden = true; return; }
  b.hidden = false; b.className = 'banner' + (info ? ' info' : ''); b.textContent = msg;
};
const say = (el, msg, bad) => { if (!el) return; el.textContent = msg || ''; el.className = 'note' + (bad ? ' bad' : ''); };

// ---- polling ---------------------------------------------------------------------------------
// Pages refresh themselves, but never while the tab is hidden (they catch up when it is shown again),
// and after IDLE_AFTER without a click or key only every IDLE_EVERY — a page left open all day must
// not fill the server log. One poller per key: a re-render replaces its poller instead of adding one.
const IDLE_AFTER = 10 * 60 * 1000, IDLE_EVERY = 30000;
let lastInput = Date.now();
['pointerdown', 'keydown', 'wheel', 'touchstart'].forEach(t =>
  window.addEventListener(t, () => { lastInput = Date.now(); }, { passive: true, capture: true }));
const pollDelay = ms => (Date.now() - lastInput > IDLE_AFTER ? Math.max(ms, IDLE_EVERY) : ms);
function every(key, fn, ms) {
  stopTimer(key);
  const t = { key, handle: 0, stopped: false, paused: false };
  const schedule = () => { if (!t.stopped) t.handle = setTimeout(tick, pollDelay(ms)); };
  const tick = () => {
    if (t.stopped) return;
    if (document.hidden) { t.paused = true; return; } // resumed by visibilitychange
    fn(); schedule();
  };
  t.resume = () => { if (t.paused && !t.stopped) { t.paused = false; tick(); } };
  timers.push(t); schedule();
}
function stopTimer(key) {
  timers.filter(t => key == null || t.key === key).forEach(t => { t.stopped = true; clearTimeout(t.handle); });
  timers = key == null ? [] : timers.filter(t => t.key !== key);
}
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) { lastInput = Date.now(); timers.slice().forEach(t => t.resume()); }
});

// ---- in-page confirmation ----------------------------------------------------------------------
// Replaces the browser's confirm(): a modal <dialog> in the page's own style (Esc / Cancel = no,
// focus starts on the safe button, Tab stays inside), built with DOM calls — nothing inline, so the
// strict CSP holds. Resolves true / false.
function ask(opts) {
  return new Promise(resolve => {
    const d = document.createElement('dialog');
    d.className = 'dlg';
    d.setAttribute('aria-labelledby', 'dlg-t');
    d.setAttribute('aria-describedby', 'dlg-m');
    const h = document.createElement('h2'); h.id = 'dlg-t'; h.textContent = opts.title || 'Are you sure?';
    const m = document.createElement('div'); m.id = 'dlg-m'; m.className = 'note'; m.textContent = opts.message || '';
    const btns = document.createElement('div'); btns.className = 'btns';
    const no = document.createElement('button'); no.type = 'button'; no.className = 'btn'; no.textContent = opts.cancel || 'Cancel';
    const yes = document.createElement('button'); yes.type = 'button'; yes.className = 'btn ' + (opts.danger ? 'danger' : 'primary');
    yes.textContent = opts.ok || 'OK';
    btns.append(yes, no); d.append(h, m, btns);
    if (opts.detail) { const x = document.createElement('div'); x.className = 'sub'; x.textContent = opts.detail; m.after(x); }
    let done = false;
    const close = v => { if (done) return; done = true; try { d.close(); } catch (e) { /* already closed */ } d.remove(); resolve(v); };
    yes.addEventListener('click', () => close(true));
    no.addEventListener('click', () => close(false));
    d.addEventListener('cancel', e => { e.preventDefault(); close(false); }); // Esc
    d.addEventListener('close', () => close(false));
    document.body.append(d);
    if (typeof d.showModal === 'function') d.showModal(); else d.setAttribute('open', '');
    no.focus();
  });
}

const KIND_TEXT = {
  catalog_unmatched_woo: 'Woo item, no POSitive match',
  catalog_unmatched_pos: 'POSitive item not created',
  catalog_ambiguous: 'Ambiguous SKU',
  catalog_link_conflict: 'Link conflict (match)',
  catalog_link_refused: 'Link refused',
  catalog_create_failed: 'Create in Woo failed',
  catalog_woo_missing: 'Woo product gone',
  push_failed: 'Price/stock push failed',
  order_unmatched_product: 'Order: item not linked',
  order_totals_mismatch: 'Order: totals differ',
  order_changed_after_import: 'Order changed after import',
  order_rejected: 'Order refused',
  order_lines_skipped: 'Order: lines skipped',
  pos_upgrade_required: 'POSitive needs Prepare database',
  job_failed: 'Job failed',
  webhook_unhealthy: 'Webhooks',
};
const kindHtml = k => '<span class="kind" title="' + esc(k) + '">' + esc(KIND_TEXT[k] || k) + '</span>';

/** An order changed after import is fixed in POSitive by hand: closing it is "Mark as handled", not "Ignore". */
const isHandledKind = k => k === 'order_changed_after_import';
const statusText = x => x.status === 'ignored' && isHandledKind(x.kind) ? 'handled' : x.status;
/** Asks, then closes the exception (Ignore / Mark as handled). True when it was closed. */
async function closeException(id, x) {
  const ok = isHandledKind(x.kind)
    ? await ask({ title: 'Mark this order as handled?', message: 'Do this once the order is fixed in POSitive (cancelled, refunded or edited to match WooCommerce). It is closed as handled. If the order changes again in Woo, it comes back as a new exception.', ok: 'Mark as handled' })
    : await ask({ title: 'Ignore this exception?', message: 'It is closed as ignored. If the same problem happens again, it comes back as a new exception.', ok: 'Ignore' });
  if (!ok) return false;
  await post('/connectors/' + enc(id) + '/exceptions/' + x.exceptionId + '/ignore');
  return true;
}

function twoClick(btn, label, armedLabel, action) {
  btn.addEventListener('click', () => {
    if (btn.dataset.armed !== '1') {
      btn.dataset.armed = '1'; btn.textContent = armedLabel;
      setTimeout(() => { btn.dataset.armed = ''; btn.textContent = label; }, 4000);
      return;
    }
    btn.dataset.armed = ''; btn.textContent = label;
    action();
  });
}

/** Polls a queued job until it is done or failed, then calls back with the job (re-render). */
function watchJob(connectorId, jobId, out, done) {
  let tries = 0;
  const tick = async () => {
    if (document.hidden) { setTimeout(tick, 5000); return; } // no calls from a hidden tab
    tries++;
    try {
      const j = await api('/connectors/' + enc(connectorId) + '/jobs/' + jobId);
      if (j.status === 'done') { say(out, 'Done (job ' + jobId + ').'); done && done(j); return; }
      if (j.status === 'failed') { say(out, 'Job ' + jobId + ' failed: ' + (j.lastError || 'see Exceptions'), true); done && done(j); return; }
      say(out, 'Job ' + jobId + ' is ' + (j.status === 'pending' ? 'waiting its turn' : j.status) + (j.lastError ? ' (last try: ' + j.lastError + ')' : '') + '…');
    } catch (e) { say(out, e.message, true); return; }
    if (tries < 200) setTimeout(tick, pollDelay(3000));
  };
  setTimeout(tick, 1500);
}

// ---- layout ---------------------------------------------------------------------------------
function setTabs(id, current) {
  const nav = $('tabs');
  const tab = (href, label, key) => '<a href="' + esc(href) + '"' + (key === current ? ' class="on"' : '') + '>' + esc(label) + '</a>';
  let html = tab('#/', 'All connectors', 'list');
  if (id) {
    const base = '#/c/' + enc(id);
    html += tab(base, 'Status', 'status') + tab(base + '/review', 'Match review', 'review')
      + tab(base + '/exceptions', 'Exceptions', 'exceptions') + tab(base + '/settings', 'Settings', 'settings');
  } else if (ME.canWrite) {
    html += tab('#/new', '+ Connect a store', 'new');
  }
  nav.innerHTML = html;
  $('crumb').textContent = id ? '· ' + id : '';
}
const view = html => { $('view').innerHTML = html; };

// ---- routing --------------------------------------------------------------------------------
async function route() {
  stopTimer(null);
  banner('');
  const parts = (location.hash.replace(/^#/, '') || '/').split('/').filter(Boolean).map(decodeURIComponent);
  try {
    if (!parts.length) return await viewList();
    if (parts[0] === 'new') return await viewWizard(null);
    if (parts[0] === 'c' && parts[1]) {
      const id = parts[1];
      if (parts[2] === 'settings') return await viewWizard(id, parts[3] || null);
      if (parts[2] === 'review') return await viewReview(id);
      if (parts[2] === 'exceptions') return await viewExceptions(id, parts[3] ? Number(parts[3]) : null);
      return await viewStatus(id);
    }
    location.hash = '#/';
  } catch (e) {
    view('<div class="card"><h2>Could not load this page</h2><div class="note bad">' + esc(e.message) + '</div>'
      + '<div class="btns"><a class="btn" href="#/">All connectors</a></div></div>');
  }
}

// ---- list -----------------------------------------------------------------------------------
function connectorDot(c) {
  if (!c.enabled) return '<span class="dot off" title="disabled"></span>';
  if (c.problems.length) return '<span class="dot badd" title="enabled but not runnable"></span>';
  return c.running ? '<span class="dot ok" title="running"></span>' : '<span class="dot warnd" title="starting"></span>';
}

async function viewList() {
  setTabs(null, 'list');
  const data = await api('/connectors');
  const cards = data.connectors.map(c => {
    const s = c.status;
    return '<div class="card"><h2 style="display:flex;gap:7px;align-items:center">' + connectorDot(c) + esc(c.id)
      + ' <span class="sub" style="margin:0">' + esc(c.storeUrl || '') + '</span></h2>'
      + '<div class="sub">' + (c.enabled ? (c.running ? 'enabled · running' : 'enabled') : 'disabled') + ' · Developer ID ' + esc(c.developerId || '—') + '</div>'
      + (c.problems.length ? '<div class="note bad">' + esc(c.problems.join(' ')) + '</div>' : '')
      + '<div class="tiles">'
      + tile('Last push', agoHtml(s.lastPushAt), 'price & stock to Woo')
      + tile('Last import', agoHtml(s.lastImportAt), 'orders into POSitive')
      + tile('Queue', esc(s.queueDepth), 'jobs waiting')
      + tile('Exceptions', '<a href="#/c/' + esc(enc(c.id)) + '/exceptions"' + (s.openExceptions ? ' class="bad"' : '') + '>' + esc(s.openExceptions) + ' open</a>',
          s.needsReview ? s.needsReview + ' item(s) to review' : 'need action')
      + '</div>'
      + '<div class="note">' + (s.webhooks.mode === 'webhooks'
          ? 'Orders by webhook' + (s.webhooks.unhealthyOpen ? ' — <span class="bad">webhooks need attention</span>' : '') + ', with a ' + esc(c.orderPollMinutes) + '-minute backstop poll.'
          : 'No tunnel address set: orders arrive by polling every ' + esc(c.orderPollMinutes) + ' min.')
      + (s.stockHoldOrders ? ' ' + esc(s.stockHoldOrders) + ' Woo order(s) hold stock not yet in POSitive.' : '') + '</div>'
      + '<div class="btns"><a class="btn primary" href="#/c/' + esc(enc(c.id)) + '">Status</a>'
      + '<a class="btn" href="#/c/' + esc(enc(c.id)) + '/review">Match review</a>'
      + '<a class="btn" href="#/c/' + esc(enc(c.id)) + '/exceptions">Exceptions</a>'
      + '<a class="btn" href="#/c/' + esc(enc(c.id)) + '/settings">Settings</a></div></div>';
  }).join('');

  view('<div class="card rows"><h2>Connector service</h2><div class="sub">POSitiveAnywhereConnectors · runs beside the server · talks to POSitive only through the public API</div>'
    + row('Version', esc(data.version)) + row('Worker', esc(data.worker))
    + (data.configError ? '<div class="note bad">' + esc(data.configError) + '</div>' : '') + '</div>'
    + (cards || '<div class="card"><h2>No store connected yet</h2><div class="note">Connect a WooCommerce store to push POSitive prices and stock to it and import its paid orders.</div>'
      + (ME.canWrite ? '<div class="btns"><a class="btn primary" href="#/new">Connect a store</a></div>'
        : '<div class="note">Connecting a store needs "Able To Access Operations Settings" (MA 106).</div>') + '</div>'));
  every('list', () => { if (location.hash === '' || location.hash === '#/') viewList().catch(() => {}); }, 20000);
}
const tile = (k, v, d) => '<div class="tile"><div class="k">' + esc(k) + '</div><div class="v">' + v + '</div><div class="d">' + esc(d) + '</div></div>';
const row = (lab, valHtml) => '<div class="row"><span class="lab">' + esc(lab) + '</span><span class="val">' + valHtml + '</span></div>';

// ---- connect wizard / settings: five steps on a rail ----------------------------------------------
// The same rail is "Connect a store" (#/new) and an existing connector's Settings
// (#/c/<id>/settings/<step>), like the Settings window's: steps listed on the left (a stepper on top
// on a phone), the open one highlighted, done ones ticked. Each step saves on its own Next; the server
// says which steps are done (connector.setup). Next stays disabled, with the reason beside it, until
// the step is complete. The rules behind it are the server's (secrets never come back, Woo keys are
// cleared when the store moves, rate limits, …).
const STEPS = [
  { key: 'store', title: 'Store connection', sub: 'address, WooCommerce keys, test' },
  { key: 'settings', title: 'Settings', sub: 'price, stock, new products, payments' },
  { key: 'key', title: 'POSitive API key', sub: 'the connector\'s own key' },
  { key: 'orders', title: 'Orders & webhooks', sub: 'how orders arrive' },
  { key: 'enable', title: 'Enable', sub: 'start it, then run the match' },
];
const stepIndex = key => STEPS.findIndex(s => s.key === key);

/**
 * The rail for connector `c` (null = a new one, nothing saved yet) with step `current` open: each
 * step's state (done / todo / attention, from the server), whether it is the open one, and whether it
 * may be opened — done and needs-attention steps always; of the others, only up to the first to do.
 */
function railModel(c, current) {
  const server = {};
  ((c && c.setup && c.setup.steps) || []).forEach(s => { server[s.key] = s; });
  const state = key => (c && server[key] ? server[key].state : 'todo');
  const firstTodo = c ? STEPS.findIndex(s => state(s.key) === 'todo') : 0;
  return STEPS.map((s, i) => ({
    key: s.key, n: i + 1, title: s.title, sub: s.sub, state: state(s.key),
    note: c && server[s.key] ? server[s.key].note : '',
    current: s.key === current,
    reachable: c ? (state(s.key) !== 'todo' || firstTodo < 0 || i <= firstTodo) : i === 0,
  }));
}

/** The step to open: the one asked for if it may be opened, else the first not done (Settings once all are). */
function openStep(c, wanted) {
  if (!c) return 'store';
  const asked = railModel(c, null).find(r => r.key === wanted);
  if (asked && asked.reachable) return asked.key;
  return (c.setup && c.setup.current) || 'settings';
}

function railHtml(rail) {
  return '<nav class="rail" aria-label="Setup steps"><ol>' + rail.map(r => {
    const mark = r.state === 'done' ? '✓' : r.state === 'attention' ? '!' : String(r.n);
    const said = r.state === 'done' ? 'done' : r.state === 'attention' ? 'needs attention' : 'to do';
    const small = r.state === 'attention' ? 'needs attention' : r.sub;
    return '<li><button type="button" class="rs ' + esc(r.state) + (r.current ? ' on' : '') + '" data-step="' + esc(r.key) + '"'
      + (r.current ? ' aria-current="step"' : '') + (r.reachable ? '' : ' disabled') + (r.note ? ' title="' + esc(r.note) + '"' : '') + '>'
      + '<span class="rm" aria-hidden="true">' + mark + '</span><span class="rt"><b>' + esc(r.title) + '</b><small>' + esc(small) + '</small></span>'
      + '<span class="vh"> — ' + said + '</span></button></li>';
  }).join('') + '</ol></nav>';
}

const trimUrl = u => (u || '').trim().replace(/\/+$/, '');
const originOf = u => { try { const x = new URL(u); return x.protocol + '//' + x.host; } catch (e) { return ''; } };
/** What a passing Test connection vouches for: this address with these typed keys (or the stored ones). */
const storeSig = f => JSON.stringify([trimUrl(f.storeUrl), f.consumerKey || '', f.consumerSecret || '']);
const storeDirty = (c, f) => !c || !!f.consumerKey || !!f.consumerSecret || trimUrl(f.storeUrl) !== trimUrl(c.storeUrl);
const numIn = (x, lo, hi) => x !== '' && x != null && Number.isFinite(Number(x)) && Number(x) >= lo && Number(x) <= hi;

/**
 * Why Next cannot be clicked yet on `step` ('' = it can). `f` = the step's form as the page reads it,
 * `c` = the saved connector (null = new), `tested` = the storeSig that last tested OK.
 */
function nextBlock(step, c, f, tested) {
  f = f || {};
  if (step === 'store') {
    if (!c && !/^[a-z0-9][a-z0-9-]{0,39}$/.test(f.id || '')) return 'Enter a connector id: lowercase letters, digits and "-" (e.g. shop1).';
    if (!/^https?:\/\/[^\s/]+/i.test(f.storeUrl || '')) return 'Enter the store address, e.g. https://shop.example.com.';
    if (!!f.consumerKey !== !!f.consumerSecret) return 'Enter the consumer key and the consumer secret together.';
    const stored = !!(c && c.secrets.wooConsumerKey && c.secrets.wooConsumerSecret);
    const moved = !!c && originOf(f.storeUrl) !== originOf(c.storeUrl);
    if (!f.consumerKey && (!stored || moved))
      return moved ? 'A new store address needs its own consumer key and secret.' : 'Enter the consumer key and secret.';
    if (storeDirty(c, f) && tested !== storeSig(f)) return 'Click Test connection — Next unlocks once the store answers.';
    return '';
  }
  if (step === 'settings') {
    if (!numIn(f.stockBuffer, 0, 1e9)) return 'The stock buffer must be 0 or more.';
    if (!numIn(f.pushIntervalSeconds, 15, 86400)) return 'Push price and stock every 15 – 86400 seconds.';
    if (!numIn(f.orderPollMinutes, 1, 1440)) return 'Poll for orders every 1 – 1440 minutes.';
    return '';
  }
  if (step === 'key') return c && c.secrets.posApiKey ? '' : 'Create or paste the connector\'s POSitive API key first.';
  if (step === 'orders') {
    if (f.webhookBaseUrl && !/^https?:\/\/[^\s/?#]+\/?$/i.test(f.webhookBaseUrl))
      return 'The public address is only the server\'s https address (e.g. https://shop1.ngrok.app) — or empty for polling only.';
    if (f.orderMode === 'since' && !f.since) return 'Choose the date the order import starts from.';
    return '';
  }
  return '';
}

async function tunnelUrl() {
  try {
    const t = await call('/ui/status/tunnel');
    const online = (t.tunnels || []).find(x => x.online && /^https:\/\//i.test(x.publicUrl || ''));
    return online ? online.publicUrl.replace(/\/+$/, '') : null;
  } catch (e) { return null; }
}

function field(name, label, inputHtml, hint) {
  return '<label for="f-' + name + '">' + esc(label) + '</label>' + inputHtml
    + (hint ? '<div class="hint">' + hint + '</div>' : '') + '<div class="err" data-err="' + name + '"></div>';
}
const input = (name, value, attrs) => '<input class="inp" id="f-' + name + '" value="' + esc(value == null ? '' : value) + '" ' + (attrs || '') + '>';
function showErrors(errors) {
  document.querySelectorAll('[data-err]').forEach(e => { e.textContent = ''; });
  Object.entries(errors || {}).forEach(([f, msgs]) => {
    const key = f.split('.')[0];
    const el = document.querySelector('[data-err="' + key + '"]') || document.querySelector('[data-err="general"]');
    if (el) el.textContent = msgs.join(' ');
  });
}

/** A one-off success line for the next render of a step: { id, step, text }. */
let wizardFlash = null;
let justEnabled = null;
/** The connector whose POSitive key was created on this page just now (its done state says "created"). */
let justMintedKey = null;

async function viewWizard(id, wanted) {
  const editing = !!id;
  if (editing) setTabs(id, 'settings'); else setTabs(null, 'new');
  if (!ME.canWrite && !editing) {
    view('<div class="card"><h2>Connect a store</h2><div class="note">Connecting a store needs the POSitive permission "Able To Access Operations Settings" (MA 106).</div></div>');
    return;
  }
  const c = editing ? await api('/connectors/' + enc(id)) : null;
  const step = openStep(c, wanted);
  if (editing && wanted && wanted !== step) history.replaceState(null, '', '#/c/' + enc(c.id) + '/settings/' + step);
  const tunnel = step === 'orders' ? await tunnelUrl() : null;
  const rail = railModel(c, step);
  const i = stepIndex(step), here = c ? rail[i] : null;
  const ro = ME.canWrite ? '' : ' disabled';
  const flash = wizardFlash && wizardFlash.id === (c ? c.id : null) && wizardFlash.step === step ? wizardFlash.text : '';
  wizardFlash = null;
  const pages = { store: stepStore, settings: stepSettings, key: stepKey, orders: stepOrders, enable: stepEnable };

  view('<div class="wiz">' + railHtml(rail) + '<div class="wiz-main"><div class="card" id="wiz-card">'
    + '<div class="sub" style="margin-bottom:2px">Step ' + (i + 1) + ' of ' + STEPS.length + (c ? ' · ' + esc(c.id) : ' · Connect a WooCommerce store') + '</div>'
    + '<h2>' + esc(STEPS[i].title)
    + (here && here.state === 'done' ? ' <span class="kind okk">✓ done</span>' : here && here.state === 'attention' ? ' <span class="kind warnk">needs attention</span>' : '') + '</h2>'
    + (here && here.state === 'attention' ? '<div class="note bad">' + esc(here.note) + '</div>' : '')
    + (!ME.canWrite ? '<div class="note">View only: changing connectors needs "Able To Access Operations Settings" (MA 106).</div>' : '')
    + (flash ? '<div class="okline" role="status">' + esc(flash) + '</div>' : '')
    + pages[step](c, ro, tunnel)
    + '<div class="err" data-err="general"></div>'
    + '<div class="wiz-foot">' + (i > 0 ? '<button class="btn" id="w-back" type="button">← Back</button>' : '')
    + (i < STEPS.length - 1 ? '<span class="why" id="w-why" role="status"></span><button class="btn primary" id="w-next" type="button">Next →</button>' : '')
    + '</div><div class="note" id="w-out" role="status"></div></div>'
    + (step === 'enable' && c && c.enabled ? nextStepsCard(c) : '')
    + '</div></div>');

  const go = key => { location.hash = c ? '#/c/' + enc(c.id) + '/settings/' + key : '#/new'; };
  document.querySelectorAll('.rail button[data-step]').forEach(b => b.addEventListener('click', () => { if (!b.disabled) go(b.dataset.step); }));
  $('w-back') && $('w-back').addEventListener('click', () => go(STEPS[i - 1].key));

  const next = $('w-next'), why = $('w-why'), out = $('w-out');
  const ctx = { refresh: () => {} };
  const wires = { store: wireStore, settings: wireSettings, key: wireKeyStep, orders: wireOrders, enable: wireEnableStep };
  const ctl = wires[step](c, ctx) || {};
  const dirty = () => ME.canWrite && !!ctl.dirty && ctl.dirty();
  const refresh = () => {
    if (!next) return;
    const reason = ME.canWrite ? nextBlock(step, c, ctl.read ? ctl.read() : {}, ctl.tested ? ctl.tested() : null) : '';
    next.disabled = !!reason;
    why.textContent = reason;
    next.textContent = dirty() ? 'Save and continue →' : 'Next →';
  };
  ctx.refresh = refresh;
  $('wiz-card').addEventListener('input', refresh);
  $('wiz-card').addEventListener('change', refresh);
  refresh();
  next && next.addEventListener('click', async () => {
    if (next.disabled) return;
    if (!dirty()) { go(STEPS[i + 1].key); return; }
    next.disabled = true;
    showErrors(null);
    say(out, 'Saving…');
    try {
      const target = await ctl.save();
      if (target) location.hash = target; else go(STEPS[i + 1].key);
    } catch (e) {
      showErrors(e.body && e.body.errors);
      say(out, e.body && e.body.errors ? 'Please fix the marked fields.' : e.message, true);
      refresh();
    }
  });

  if (step === 'enable' && c && c.enabled) {
    wireNextSteps(c);
    if (justEnabled === c.id && $('next-steps')) {
      justEnabled = null;
      $('next-steps').scrollIntoView({ behavior: 'smooth', block: 'start' });
      $('next-steps').focus({ preventScroll: true });
    }
  }
}

// ---- step 1: store connection ------------------------------------------------------------------------
function stepStore(c, ro) {
  const stored = ok => ok ? 'stored ✓ — leave both empty to keep them' : 'not stored yet';
  return '<div class="sub">Create a REST API key in WordPress: WooCommerce → Settings → Advanced → REST API → Add key, permissions Read/Write.</div>'
    + '<div class="form">'
    + (c ? '' : field('id', 'Connector id', input('id', '', 'maxlength="40" autocomplete="off" spellcheck="false" placeholder="shop1"' + ro),
        'Lowercase letters, digits and "-". It is part of the webhook address and cannot be changed later.'))
    + field('storeUrl', 'Store address', input('storeUrl', c ? c.storeUrl : '', 'placeholder="https://shop.example.com" autocomplete="off" spellcheck="false"' + ro))
    + field('consumerKey', 'Consumer key', input('consumerKey', '', 'type="password" autocomplete="off" placeholder="ck_…"' + ro),
        c ? stored(c.secrets.wooConsumerKey) : 'Saved encrypted on the server; never shown again.')
    + field('consumerSecret', 'Consumer secret', input('consumerSecret', '', 'type="password" autocomplete="off" placeholder="cs_…"' + ro),
        c ? stored(c.secrets.wooConsumerSecret) : '')
    + '</div><div class="btns"><button class="btn" id="b-test" type="button"' + ro + '>Test connection</button></div><div id="test-out" class="note" role="status"></div>';
}

function wireStore(c, ctx) {
  let tested = null;
  const read = () => ({
    id: c ? c.id : $('f-id').value.trim().toLowerCase(),
    storeUrl: $('f-storeUrl').value.trim(), consumerKey: $('f-consumerKey').value.trim(), consumerSecret: $('f-consumerSecret').value.trim(),
  });
  $('b-test').addEventListener('click', async () => {
    const out = $('test-out'), f = read();
    tested = null; ctx.refresh();
    say(out, 'Testing…');
    try {
      const r = await post('/test-connection', { storeUrl: f.storeUrl, consumerKey: f.consumerKey, consumerSecret: f.consumerSecret, connectorId: c ? c.id : null });
      if (!r.ok) { say(out, r.error, true); return; }
      tested = storeSig(f);
      const i = r.info, yn = b => b == null ? 'unknown' : b ? 'yes' : 'no';
      out.className = 'note good';
      out.innerHTML = '✓ Connected to ' + esc(r.storeUrl) + ' — ' + esc(i.productCount == null ? '?' : i.productCount) + ' products · currency '
        + esc(i.currency || '?') + ' · ' + esc(i.priceDecimals == null ? '?' : i.priceDecimals) + ' decimals · taxes ' + esc(yn(i.taxesEnabled))
        + ' · prices include tax ' + esc(yn(i.pricesIncludeTax)) + ' · HPOS ' + esc(yn(i.highPerformanceOrderStorage));
    } catch (e) {
      say(out, e.body && e.body.errors ? Object.values(e.body.errors).flat().join(' ') : e.message, true);
    } finally { ctx.refresh(); }
  });
  return {
    read, tested: () => tested, dirty: () => storeDirty(c, read()),
    save: async () => {
      const f = read();
      const body = { storeUrl: f.storeUrl, consumerKey: f.consumerKey || null, consumerSecret: f.consumerSecret || null };
      if (!c) {
        // Saved disabled with the default settings; steps 2 and 4 confirm them.
        const saved = await post('/connectors', { ...body, id: f.id });
        wizardFlash = { id: saved.id, step: 'settings', text: '✓ Store saved as connector "' + saved.id + '" (disabled for now). Check the settings next.' };
        return '#/c/' + enc(saved.id) + '/settings/settings';
      }
      await put('/connectors/' + enc(c.id), body);
      ['consumerKey', 'consumerSecret'].forEach(n => { $('f-' + n).value = ''; });
      wizardFlash = { id: c.id, step: 'settings', text: '✓ Store connection saved.' };
      return null;
    },
  };
}

// ---- step 2: settings ----------------------------------------------------------------------------------
function stepSettings(c, ro) {
  const v = c;
  return '<div class="sub">POSitive owns price and stock; Woo owns new orders. The defaults are the approved ones.</div><div class="form">'
    + field('priceLevel', 'POSitive price level', '<select class="inp" id="f-priceLevel"' + ro + '>' + [1, 2, 3, 4, 5, 6].map(n => '<option' + (n === v.priceLevel ? ' selected' : '') + '>' + n + '</option>').join('') + '</select>', 'Sent to Woo as the regular price (its sale price too, when one is active).')
    + field('stockBuffer', 'Stock buffer', input('stockBuffer', v.stockBuffer, 'type="number" min="0" step="1"' + ro), 'Units held back from the web: Woo gets available − buffer, never below 0.')
    + field('publishNewAs', 'New products in Woo', '<select class="inp" id="f-publishNewAs"' + ro + '><option value="draft"' + (v.publishNewAs === 'draft' ? ' selected' : '') + '>draft (review before they go live)</option><option value="publish"' + (v.publishNewAs === 'publish' ? ' selected' : '') + '>published</option></select>')
    + field('overwriteTitleDescription', 'Titles & descriptions', '<label class="check"><input type="checkbox" id="f-overwriteTitleDescription"' + (v.overwriteTitleDescription ? ' checked' : '') + ro + '> overwrite them in Woo on every push</label>', 'Off: sent only when the connector creates a product (Woo text is usually richer).')
    + field('importOnHold', 'On-hold orders', '<label class="check"><input type="checkbox" id="f-importOnHold"' + (v.importOnHold ? ' checked' : '') + ro + '> also import on-hold orders (bank transfer, cheque)</label>', 'Off: only paid orders (processing / completed) are imported.')
    + field('payOnCompletionMethods', 'Paid on completion', input('payOnCompletionMethods', (v.payOnCompletionMethods || []).join(', '), 'placeholder="cod" spellcheck="false"' + ro), 'Woo payment method ids (comma separated) paid on delivery: the order is imported at processing, its payment only once completed.')
    + field('paymentTenders', 'Payments post as', '<div id="f-paymentTenders" class="tenders"><span class="muted">Reading the store\'s payment methods…</span></div>', 'The POSitive tender each Woo payment method is posted as. A method not listed posts as charge card. POSitive\'s API has no cash tender yet.')
    + field('pushIntervalSeconds', 'Price/stock push every', input('pushIntervalSeconds', v.pushIntervalSeconds, 'type="number" min="15" max="86400"' + ro), 'seconds (15 – 86400)')
    + field('orderPollMinutes', 'Order poll every', input('orderPollMinutes', v.orderPollMinutes, 'type="number" min="1" max="1440"' + ro), 'minutes — the backstop for missed webhooks, or the only way in without a public address')
    + '</div>';
}

const TENDER_LABELS = { card: 'Charge card (R)', check: 'Check (C)', onAccount: 'On account (A)' };

/** The payment-method → tender map as it is saved: card rows left out (card is the default). */
function tendersFrom(map) {
  const out = {};
  Object.keys(map || {}).sort().forEach(k => { if (map[k] && map[k] !== 'card') out[k] = map[k]; });
  return out;
}

/** One row per enabled Woo gateway (plus mapped methods Woo no longer lists), each with a tender dropdown. */
async function loadTenders(c, ro, ctx) {
  const box = $('f-paymentTenders');
  if (!box) return;
  let r;
  try { r = await api('/connectors/' + enc(c.id) + '/payment-gateways'); } catch (e) { r = null; }
  if (!$('f-paymentTenders')) return; // the page moved on
  const rows = r ? r.gateways : Object.keys(c.paymentTenders || {}).map(id => ({ id, title: null, enabled: false, tender: c.paymentTenders[id] }));
  const tenders = r ? r.tenders : Object.keys(TENDER_LABELS);
  const note = !r || !r.read ? '<div class="note">The store\'s payment methods could not be read just now; only the mapped ones are shown. Reopen this step to try again.</div>' : '';
  if (!rows.length) { box.innerHTML = note + '<span class="muted">No payment methods are enabled in WooCommerce. Every payment posts as charge card.</span>'; ctx.refresh(); return; }
  box.innerHTML = note + '<table><tbody>' + rows.map(g => '<tr><td><b>' + esc(g.title || g.id) + '</b> <code>' + esc(g.id) + '</code>'
      + (g.enabled ? '' : ' <span class="muted">(not enabled in Woo)</span>') + '</td><td>'
      + '<select class="inp" data-method="' + esc(g.id) + '" aria-label="' + esc('Tender for ' + (g.title || g.id)) + '"' + ro + '>'
      + tenders.map(t => '<option value="' + esc(t) + '"' + (t === g.tender ? ' selected' : '') + '>' + esc(TENDER_LABELS[t] || t) + '</option>').join('')
      + '</select></td></tr>').join('') + '</tbody></table>';
  ctx.refresh();
}

function wireSettings(c, ctx) {
  loadTenders(c, ME.canWrite ? '' : ' disabled', ctx);
  const tenderRead = () => {
    const sel = document.querySelectorAll('#f-paymentTenders select[data-method]');
    if (!sel.length) return tendersFrom(c.paymentTenders);
    const map = {};
    sel.forEach(s => { map[s.dataset.method] = s.value; });
    return tendersFrom(map);
  };
  const read = () => ({
    priceLevel: $('f-priceLevel').value, stockBuffer: $('f-stockBuffer').value.trim(), publishNewAs: $('f-publishNewAs').value,
    overwriteTitleDescription: $('f-overwriteTitleDescription').checked, importOnHold: $('f-importOnHold').checked,
    payOnCompletionMethods: $('f-payOnCompletionMethods').value.split(',').map(s => s.trim()).filter(Boolean),
    pushIntervalSeconds: $('f-pushIntervalSeconds').value.trim(), orderPollMinutes: $('f-orderPollMinutes').value.trim(),
    paymentTenders: tenderRead(),
  });
  const body = f => ({
    priceLevel: Number(f.priceLevel), stockBuffer: Number(f.stockBuffer), publishNewAs: f.publishNewAs,
    overwriteTitleDescription: f.overwriteTitleDescription, importOnHold: f.importOnHold, payOnCompletionMethods: f.payOnCompletionMethods,
    paymentTenders: f.paymentTenders,
    pushIntervalSeconds: Number(f.pushIntervalSeconds), orderPollMinutes: Number(f.orderPollMinutes),
  });
  const saved = JSON.stringify(body(read()));
  const done = c.setup.steps.find(s => s.key === 'settings').state === 'done';
  return {
    read, dirty: () => !done || JSON.stringify(body(read())) !== saved,
    save: async () => {
      await put('/connectors/' + enc(c.id), { ...body(read()), setupStep: 'settings' });
      wizardFlash = { id: c.id, step: 'key', text: '✓ Settings saved.' };
      return null;
    },
  };
}

// ---- step 3: the connector's own POSitive API key ----------------------------------------------
function stepKey(c) {
  const devId = c.developerId || ('WOO-' + c.id);
  const w = ME.canWrite;
  const intro = '<div class="sub">The connector calls POSitive with its own key, under Developer ID <b>' + esc(devId) + '</b>, so this store\'s links (customers, orders, items) stay its own.</div>';
  const create = '<div class="form"><label for="k-name">Key name</label><input class="inp" id="k-name" maxlength="100" value="' + esc('WooCommerce connector - ' + c.id) + '">'
    + '<label>Developer ID</label><div><code>' + esc(devId) + '</code></div></div>'
    + '<div id="k-confirm" hidden><div class="note bad" id="k-confirm-msg"></div><div class="btns">'
    + '<button class="btn danger" id="b-mint-share" type="button">Create key sharing this Developer ID</button><button class="btn" id="b-mint-no" type="button">Cancel</button></div></div>';
  const paste = '<div class="btns"><input class="inp" id="k-paste" type="password" autocomplete="off" placeholder="paste the API key" aria-label="POSitive API key" style="flex:1;min-width:200px"><button class="btn" id="b-paste" type="button">Use this key</button></div>'
    + '<div id="k-mismatch" hidden><div class="note bad" id="k-mismatch-msg"></div><div class="btns"><button class="btn danger" id="b-paste-confirm" type="button">Use it and switch Developer ID</button></div></div>';
  const out = '<div id="key-out" class="note" role="status"></div>';

  if (c.secrets.posApiKey) {
    // Done: say so first and plainly; replacing it is one quiet button away.
    return intro
      + '<div class="okline big" id="key-state" role="status">✓ ' + (justMintedKey === c.id ? 'Your key was created and is stored for this connector' : 'A key is stored for this connector')
      + ' (Developer ID ' + esc(c.developerId || devId) + ').</div>'
      + (w ? '<div class="btns" style="margin-top:6px"><button class="btn small" id="b-verify" type="button">Check it</button>'
        + '<button class="btn small quiet" id="b-replace" type="button" aria-expanded="false" aria-controls="k-more">Replace key…</button></div>' : '')
      + out
      + (w ? '<div id="k-more" hidden><h3>Create a new key</h3><div class="note">Replacing a lost or leaked key: it is made on the same Developer ID (you are asked to confirm the share), then revoke the old one on the ops console.</div>'
        + create + '<div class="btns"><button class="btn" id="b-mint" type="button">Create a new key</button></div>'
        + '<h3>Or paste a key</h3><div class="note">Made on the ops console (API keys card).</div>' + paste + '</div>' : '');
  }
  if (!w) return intro + '<div class="note"><b>No key stored yet.</b></div>' + out;
  return intro + '<div class="note"><b>No key stored yet.</b> Create one here in one click — it is handed straight to the connector and never shown.</div>'
    + '<h3>Create one now</h3><div class="note">Needs "Able To Create API Keys" (MA 107).</div>'
    + create + '<div class="btns"><button class="btn primary" id="b-mint" type="button">Create key for this connector</button></div>'
    + out
    + '<details class="alt"><summary>Or paste a key made on the ops console</summary><div class="note">For example when someone with MA 107 made it for you on the API keys card (Developer ID ' + esc(devId) + ').</div>' + paste + '</details>';
}

function wireKeyStep(c) {
  const out = $('key-out');
  const devId = c.developerId || ('WOO-' + c.id);
  const reload = () => { stopTimer(null); route(); };
  $('b-verify') && $('b-verify').addEventListener('click', async () => {
    say(out, 'Checking with /api/whoami…');
    try {
      const r = await post('/connectors/' + enc(c.id) + '/pos-key/verify');
      say(out, r.ok ? '✓ The key works: Developer ID ' + r.developerId + '.' : r.error, !r.ok);
      if (r.ok) out.className = 'note good';
    } catch (e) { say(out, e.message, true); }
  });
  $('b-replace') && $('b-replace').addEventListener('click', () => {
    const more = $('k-more');
    more.hidden = !more.hidden;
    $('b-replace').setAttribute('aria-expanded', String(!more.hidden));
    if (!more.hidden) $('k-name').focus();
  });
  if (!ME.canWrite || !$('b-mint')) return {};

  // Hand a key to the connector. The key value lives only in this function's variables.
  const store = async (apiKey, confirmDeveloperId) => {
    await post('/connectors/' + enc(c.id) + '/pos-key', { apiKey, confirmDeveloperId: !!confirmDeveloperId });
  };

  let pending = null; // { name } the share-confirm button is armed for
  const disarm = () => { pending = null; $('k-confirm').hidden = true; };
  $('k-name').addEventListener('input', disarm);
  $('b-mint-no').addEventListener('click', () => { disarm(); say(out, 'Not created.'); });

  const mint = async confirmShare => {
    const name = $('k-name').value.trim();
    if (!name) { say(out, 'Enter a name for the key.', true); return; }
    if (confirmShare && !(pending && pending.name === name)) { disarm(); return; }
    const body = { name, developerId: devId };
    if (confirmShare) body.confirmShareDeveloperId = true; // only ever from the separate confirm button
    const buttons = [$('b-mint'), $('b-mint-share')];
    buttons.forEach(b => { b.disabled = true; });
    let created = null;
    try {
      say(out, 'Creating the key…');
      created = await call('/ui/api-keys', { method: 'POST', body: JSON.stringify(body) });
      disarm();
      say(out, 'Key created; handing it to the connector…');
      await store(created.key);
      // Straight to the done state: the success line replaces the create button, Next unlocks.
      justMintedKey = c.id;
      const shares = created.sharesDeveloperIdWith && created.sharesDeveloperIdWith.length ? created.sharesDeveloperIdWith.join(', ') : '';
      if (shares) wizardFlash = { id: c.id, step: 'key', text: 'It shares links with: ' + shares + ' — revoke the old key on the ops console when it is no longer used.' };
      reload();
    } catch (e) {
      if (!created && e.status === 409 && e.body.errorCode === 'developer_id_in_use') {
        pending = { name };
        $('k-confirm-msg').textContent = 'Developer ID "' + (e.body.developerId || devId) + '" is already used by: '
          + (e.body.sharesDeveloperIdWith || []).join(', ') + '. A new key on it shares their links and data. '
          + 'Only continue to replace this connector\'s own key (e.g. it was lost); otherwise stop here.';
        $('k-confirm').hidden = false;
        say(out, '');
      } else if (!created && e.status === 409 && e.body.errorCode === 'name_taken') {
        say(out, 'A key called "' + name + '" already exists. Change the key name, then click Create again.', true);
      } else if (!created && e.status === 403 && !e.body.errorCode) {
        say(out, 'Creating keys needs the POSitive permission "Able To Create API Keys" (MA 107). Ask whoever manages POSitive security, or have them create the key on the ops console and paste it below.', true);
      } else if (created) {
        // Created but not stored: show it once so it is not lost, and say what to do.
        out.className = 'note bad';
        out.innerHTML = 'The key was created but the connector did not take it: ' + esc(e.message)
          + '<br>Copy it now and paste it in the box below (or revoke it on the ops console): <code style="user-select:all">' + esc(created.key) + '</code>';
        const alt = document.querySelector('details.alt'); if (alt) alt.open = true;
      } else {
        say(out, e.message, true);
      }
    } finally {
      created = null;
      buttons.forEach(b => { b.disabled = false; });
    }
  };
  $('b-mint').addEventListener('click', () => mint(false));
  $('b-mint-share').addEventListener('click', () => mint(true));

  let pastedForConfirm = false;
  const paste = async confirm => {
    const key = $('k-paste').value.trim();
    if (!key) { say(out, 'Paste the key first.', true); return; }
    if (confirm && !pastedForConfirm) return;
    say(out, 'Checking the key with /api/whoami…');
    try {
      await store(key, confirm);
      $('k-paste').value = '';
      $('k-mismatch').hidden = true;
      justMintedKey = null;
      wizardFlash = { id: c.id, step: 'key', text: 'Key checked with /api/whoami and stored.' };
      reload();
    } catch (e) {
      if (e.status === 409 && e.body.errorCode === 'developer_id_mismatch') {
        pastedForConfirm = true;
        $('k-mismatch-msg').textContent = e.message;
        $('k-mismatch').hidden = false;
        say(out, '');
      } else say(out, e.message, true);
    }
  };
  $('k-paste').addEventListener('input', () => { pastedForConfirm = false; $('k-mismatch').hidden = true; });
  $('b-paste').addEventListener('click', () => paste(false));
  $('b-paste-confirm').addEventListener('click', () => paste(true));
  // The pasted key never outlives the page (back/forward cache included).
  window.addEventListener('pagehide', () => { const p = $('k-paste'); if (p) p.value = ''; }, { once: true });
  return {};
}

// ---- step 4: orders & webhooks -------------------------------------------------------------------------
function stepOrders(c, ro, tunnel) {
  const pending = c.setup.steps.find(s => s.key === 'orders').state === 'todo';
  // While this step is still to do, the public address is offered from the server's online tunnel.
  const address = c.webhookBaseUrl || (pending && tunnel) || '';
  const s = c.status;
  return '<div class="sub">Woo can send each new order at once (webhooks, through this server\'s public address), or the connector fetches them every ' + esc(c.orderPollMinutes) + ' min (polling).</div>'
    + '<div class="form">'
    + field('webhookBaseUrl', 'Public address (webhooks)', input('webhookBaseUrl', address, 'placeholder="https://shop1.ngrok.app" spellcheck="false"' + ro),
        (tunnel ? 'This server\'s tunnel is online at ' + esc(tunnel) + '. ' : '')
        + 'Woo sends orders to &lt;address&gt;/hooks/woocommerce/' + esc(c.id) + ' at once. <b>Empty = polling only</b>: orders arrive within the poll interval.')
    + (pending
      ? field('orderStart', 'Import orders', '<div><label class="check"><input type="radio" name="os" value="now" checked' + ro + '> from now on (history is not imported)</label>'
        + '<label class="check" style="margin-top:4px"><input type="radio" name="os" value="since"' + ro + '> modified since <input class="inp" id="f-since" type="date" aria-label="since" style="width:auto;margin-left:6px"' + ro + '></label></div>')
      : field('orderStart', 'Orders imported from', '<div><b>' + esc(when(s && s.ordersCursor)) + '</b></div>'))
    + '</div>'
    + (!pending && ME.canWrite ? '<h3>Move where the order import starts</h3><div class="note">Moving it back imports Woo orders modified since then (already imported ones are recognised); moving it forward skips older changes. Applies at once.</div>'
      + '<div class="btns"><input class="inp" type="date" id="os-date" aria-label="import since" style="width:auto"><button class="btn" id="b-os-since" type="button">Import since this date</button><button class="btn" id="b-os-now" type="button">From now</button></div><div class="note" id="os-out" role="status"></div>' : '');
}

function wireOrders(c) {
  const pending = c.setup.steps.find(s => s.key === 'orders').state === 'todo';
  const read = () => {
    const mode = (document.querySelector('input[name="os"]:checked') || {}).value || 'now';
    return { webhookBaseUrl: $('f-webhookBaseUrl').value.trim(), orderMode: pending ? mode : null, since: pending && $('f-since') ? $('f-since').value : null };
  };
  const setStart = async body => {
    try { const x = await post('/connectors/' + enc(c.id) + '/order-start', body); say($('os-out'), 'Order import now starts at ' + when(x.ordersCursor) + '.'); }
    catch (e) { say($('os-out'), e.message, true); }
  };
  $('b-os-now') && twoClick($('b-os-now'), 'From now', 'Sure? Click again', () => setStart({ mode: 'now' }));
  $('b-os-since') && $('b-os-since').addEventListener('click', () => setStart({ mode: 'since', since: $('os-date').value || null }));
  return {
    read, dirty: () => pending || trimUrl(read().webhookBaseUrl) !== trimUrl(c.webhookBaseUrl),
    save: async () => {
      const f = read();
      if (pending) await post('/connectors/' + enc(c.id) + '/order-start', f.orderMode === 'since' ? { mode: 'since', since: f.since } : { mode: 'now' });
      await put('/connectors/' + enc(c.id), { webhookBaseUrl: f.webhookBaseUrl, setupStep: 'orders' });
      wizardFlash = { id: c.id, step: 'enable', text: '✓ Orders: ' + (f.webhookBaseUrl ? 'by webhook, with a ' + c.orderPollMinutes + '-minute backstop poll.' : 'polling every ' + c.orderPollMinutes + ' min.') };
      return null;
    },
  };
}

// ---- step 5: enable / disable / remove ------------------------------------------------------------
function stepEnable(c) {
  const before = c.setup.steps.filter(s => s.key !== 'enable' && s.state !== 'done');
  return '<div class="note">' + (c.enabled
      ? 'The connector is running: price/stock every ' + esc(c.pushIntervalSeconds) + ' s, orders by ' + (c.webhookBaseUrl ? 'webhook and ' : '') + 'poll every ' + esc(c.orderPollMinutes) + ' min.'
      : 'When enabled, the first push runs the match (Woo SKU = POSitive barcode) and creates POSitive web items Woo lacks as ' + esc(c.publishNewAs) + '. Review it under Match review.') + '</div>'
    + (!c.enabled && before.length ? '<div class="note bad">Finish ' + esc(before.map(s => s.number + ' ' + s.title).join(', ')) + ' first.</div>' : '')
    + (c.problems.length ? '<div class="note bad">' + esc(c.problems.join(' ')) + '</div>' : '')
    + (ME.canWrite ? '<div class="btns">' + (c.enabled ? '<button class="btn" id="b-disable" type="button">Disable</button>'
        : '<button class="btn primary" id="b-enable" type="button"' + (before.length ? ' disabled' : '') + '>Enable connector</button>')
      + '<button class="btn danger" id="b-remove" type="button">Remove connector</button></div>' : '')
    + '<div class="note" id="en-out" role="status"></div>';
}

function wireEnableStep(c) {
  const out = $('en-out');
  $('b-enable') && $('b-enable').addEventListener('click', async () => {
    try {
      await post('/connectors/' + enc(c.id) + '/enable');
      say(out, 'Enabled. Next steps are below.');
      justEnabled = c.id;
      route();
    } catch (e) { say(out, e.message, true); }
  });
  $('b-disable') && $('b-disable').addEventListener('click', async () => {
    try { await post('/connectors/' + enc(c.id) + '/disable'); route(); }
    catch (e) { say(out, e.message, true); }
  });
  $('b-remove') && twoClick($('b-remove'), 'Remove connector', 'Sure? Click again to remove', async () => {
    try {
      const r = await api('/connectors/' + enc(c.id), { method: 'DELETE' });
      banner('Removed "' + r.removed + '". ' + r.note, true);
      location.hash = '#/';
    } catch (e) { say(out, e.message, true); }
  });
  return {};
}

// ---- next steps (after Enable, and on Status until the first match is reviewed) -----------------
const plural = (n, one, many) => n + ' ' + (n === 1 ? one : (many || one + 's'));

/** The four things a store owner does after connecting, in plain words. */
function nextStepsCard(c) {
  const s = c.status, m = s.match, id = enc(c.id), w = ME.canWrite;
  const sum = m.summary;
  let matchState;
  if (m.runningJobId) {
    matchState = 'The match is ' + (m.runningStatus === 'running' ? 'running now' : 'waiting its turn') + ' (job ' + esc(m.runningJobId) + '). This takes a minute or two for a few hundred products.';
  } else if (m.done) {
    matchState = 'Last run ' + (m.lastRunAt ? agoHtml(m.lastRunAt) : 'when the connector was enabled')
      + (sum ? ': ' + esc(plural(sum.matched, 'item') + ' paired, ' + plural(sum.created, 'created in Woo', 'created in Woo')
        + ', ' + plural((sum.unmatchedWoo || 0) + (sum.unmatchedPos || 0) + (sum.ambiguous || 0), 'item') + ' to review') : '') + '.';
  } else if (!c.enabled) {
    matchState = 'It runs once the connector is enabled.';
  } else {
    matchState = 'It starts on its own within a minute of enabling, or run it now.';
  }
  const li = (n, title, text, actions) => '<li><div class="ns-n" aria-hidden="true">' + n + '</div><div class="ns-b"><b>' + esc(title) + '</b>'
    + '<div class="note">' + text + '</div>' + (actions ? '<div class="btns">' + actions + '</div>' : '') + '</div></li>';
  return '<div class="card next" id="next-steps" tabindex="-1"><h2>Next steps</h2><div class="sub">what to do now that the store is connected</div><ol class="ns">'
    + li(1, 'Run the match', 'The connector pairs each Woo product with the POSitive item that has the same SKU or barcode, then sends POSitive\'s price and stock to Woo. ' + matchState,
      (w ? '<button class="btn' + (m.done ? '' : ' primary') + '" id="ns-match" type="button"' + (m.runningJobId ? ' disabled' : '') + '>' + (m.done ? 'Run it again' : 'Run the match') + '</button>' : '')
      + '<span class="note" id="ns-out" role="status"></span>')
    + li(2, 'Review the matches', s.needsReview
        ? esc(plural(s.needsReview, 'item')) + ' to look at — mostly Woo products with no POSitive item, which is normal. Link the ones you want POSitive to manage, ignore the rest.'
        : (m.done ? 'Nothing waiting. New products show up here after each match.' : 'After the match, anything it could not pair shows up here.'),
      '<a class="btn' + (m.done && s.needsReview ? ' primary' : '') + '" href="#/c/' + id + '/review">Open Match review' + (s.needsReview ? ' (' + esc(s.needsReview) + ')' : '') + '</a>')
    + li(3, 'Keep an eye on it', 'Prices and stock go to Woo every ' + esc(c.pushIntervalSeconds) + ' s; paid Woo orders come into POSitive by themselves. '
        + 'Anything that needs you — an order that could not be imported, a refused link — is listed under Exceptions'
        + (s.openExceptions ? ' (<span class="bad">' + esc(s.openExceptions) + ' now</span>)' : ' (none now)') + '.',
      '<a class="btn" href="#/c/' + id + '">Status</a><a class="btn" href="#/c/' + id + '/exceptions">Exceptions</a>')
    + li(4, 'Back to the Server console', 'The connector keeps running in the background; you can close this page.',
      '<a class="btn" href="/ops">← Server console</a>')
    + '</ol></div>';
}

function wireNextSteps(c) {
  const out = $('ns-out');
  const m = c.status.match;
  // A match already queued or running: say when it is done, then show its result.
  if (m.runningJobId) watchJob(c.id, m.runningJobId, out, () => setTimeout(route, 800));
  $('ns-match') && $('ns-match').addEventListener('click', async () => {
    $('ns-match').disabled = true;
    try {
      const q = await post('/connectors/' + enc(c.id) + '/review/match', { createMissing: true });
      say(out, 'The match is queued (job ' + q.jobId + ').' + (q.note ? ' ' + q.note : ''));
      watchJob(c.id, q.jobId, out, () => setTimeout(route, 800));
    } catch (e) { $('ns-match').disabled = false; say(out, e.message, true); }
  });
}

// ---- status -------------------------------------------------------------------------------------
async function viewStatus(id) {
  setTabs(id, 'status');
  const r = await api('/connectors/' + enc(id) + '/status');
  const c = r.connector, s = c.status;
  const showNext = c.enabled && (!s.match.done || s.needsReview > 0 || s.match.runningJobId);
  const jobs = r.jobs.map(j => '<tr><td>' + esc(j.kind) + (j.resourceKey ? ' <span class="sub">' + esc(j.resourceKey) + '</span>' : '') + '</td><td>'
    + esc(j.status) + (j.failedPermanently ? ' (permanent)' : '') + '</td><td class="num">' + esc(j.attempts) + '</td><td>' + agoHtml(j.updatedAt) + '</td><td>' + esc(j.lastError || '') + '</td></tr>').join('');
  view((showNext ? nextStepsCard(c) : '')
    + '<div class="card"><h2 style="display:flex;gap:7px;align-items:center">' + connectorDot(c) + esc(c.id) + ' <span class="sub" style="margin:0">' + esc(c.storeUrl || '') + '</span></h2>'
    + '<div class="sub">' + (c.enabled ? (c.running ? 'enabled · running' : 'enabled · waiting for the worker') : 'disabled') + ' · worker: ' + esc(r.worker) + '</div>'
    + (c.problems.length ? '<div class="note bad">' + esc(c.problems.join(' ')) + '</div>' : '')
    + '<div class="tiles">'
    + tile('Last push', agoHtml(s.lastPushAt), 'price & stock to Woo')
    + tile('Last import', agoHtml(s.lastImportAt), 'orders into POSitive')
    + tile('Exceptions', '<a href="#/c/' + esc(enc(id)) + '/exceptions"' + (s.openExceptions ? ' class="bad"' : '') + '>' + esc(s.openExceptions) + '</a>',
        'need action · ' + r.exceptions.resolved + ' resolved · ' + r.exceptions.ignored + ' ignored')
    + tile('Needs review', '<a href="#/c/' + esc(enc(id)) + '/review">' + esc(s.needsReview) + '</a>', 'Match review — not errors')
    + tile('Queue', esc(s.queueDepth), 'jobs waiting or running')
    + tile('Linked items', esc(s.linkedItems), 'POSitive ⇄ Woo')
    + tile('Stock holds', esc(s.stockHoldOrders), s.stockHoldItems + ' item(s) held back for Woo orders not in POSitive')
    + tile('Webhooks', s.webhooks.mode === 'webhooks' ? (s.webhooks.unhealthyOpen ? '<span class="bad">attention</span>' : 'on') : 'polling only',
        s.webhooks.mode === 'webhooks' ? 'checked ' + ago(s.lastWebhookCheckAt) : 'no public address set')
    + '</div><div class="rows">'
    + row('Last order poll', agoHtml(s.lastPollAt))
    + row('Webhook address', s.webhooks.deliveryUrl ? '<code>' + esc(s.webhooks.deliveryUrl) + '</code>' : '—')
    + row('Orders imported from', esc(when(s.ordersCursor)))
    + row('Catalog change feed at', esc(when(s.catalogCursor)))
    + row('Developer ID', esc(c.developerId || '—'))
    + row('Keys stored', esc([c.secrets.wooConsumerKey && c.secrets.wooConsumerSecret ? 'Woo' : null, c.secrets.posApiKey ? 'POSitive' : null, c.secrets.wooWebhookSecret ? 'webhook secret' : null].filter(Boolean).join(', ') || 'none'))
    + '</div>'
    + (ME.canWrite ? '<h3>Where the order import starts</h3><div class="note">Moving it back imports Woo orders modified since then (already imported ones are recognised); moving it forward skips older changes.</div>'
      + '<div class="btns"><input class="inp" type="date" id="os-date" style="width:auto"><button class="btn" id="b-os-since" type="button">Import since this date</button><button class="btn" id="b-os-now" type="button">From now</button></div><div class="note" id="os-out"></div>' : '')
    + '</div>'
    + '<div class="card"><h2>Recent jobs</h2><div class="sub">newest first · refreshes every 10 s while this tab is open (every 30 s after 10 minutes without a click)</div><div class="tscroll"><table><tr><th>Job</th><th>Status</th><th class="num">Tries</th><th>Updated</th><th>Last error</th></tr>'
    + (jobs || '<tr><td colspan="5" class="sub">no jobs yet</td></tr>') + '</table></div></div>');
  if (showNext) wireNextSteps(c);
  const setStart = async body => {
    try { const x = await post('/connectors/' + enc(id) + '/order-start', body); say($('os-out'), 'Order import now starts at ' + when(x.ordersCursor) + '.'); }
    catch (e) { say($('os-out'), e.message, true); }
  };
  $('b-os-now') && twoClick($('b-os-now'), 'From now', 'Sure? Click again', () => setStart({ mode: 'now' }));
  $('b-os-since') && $('b-os-since').addEventListener('click', () => setStart({ mode: 'since', since: $('os-date').value || null }));
  // One poller (re-rendering replaces it); not while typing, not while a job is being watched here.
  every('status', () => {
    if (location.hash !== '#/c/' + enc(id) || document.activeElement.matches('input')) return;
    if ($('ns-out') && $('ns-out').textContent && !/^Done|failed/.test($('ns-out').textContent)) return;
    viewStatus(id).catch(() => {});
  }, 10000);
}

// ---- POSitive item picker ---------------------------------------------------------------------------
/** "linked to Woo #58" for a picker row. */
const linkedTag = p => p.linkedWooId ? ' <span class="kind warnk">linked to Woo #' + esc(p.linkedWooId) + '</span>' : '';

function picker(holder, connectorId, onPick, wooId) {
  holder.innerHTML = '<div class="picker"><div class="btns" style="margin-top:0"><input class="inp" placeholder="item number, SKU/barcode or description" aria-label="Search POSitive items" style="flex:1;min-width:180px">'
    + '<button class="btn" type="button">Search POSitive</button><button class="btn" type="button">Close</button></div><div class="note" role="status"></div><div class="tscroll"></div></div>';
  const [q, go, close] = holder.querySelectorAll('input,button');
  const out = holder.querySelector('.note'), list = holder.querySelector('.tscroll');
  const search = async () => {
    const text = q.value.trim();
    if (!text) return;
    say(out, 'Searching…'); list.innerHTML = '';
    try {
      const r = await api('/connectors/' + enc(connectorId) + '/pos-products?q=' + enc(text));
      const missing = r.unavailable || [];
      say(out, r.items.length ? 'Choose the POSitive item:' : (missing.length ? 'Nothing could be found right now.' : 'Nothing found. Try the barcode or another word.'));
      const rows = r.items.map((p, i) =>
        '<tr><td>' + esc(p.invNo) + '</td><td>' + esc(p.sku || '') + '</td><td>' + esc(p.description || '')
        // webProduct null = unknown: no tag (and no warning on Choose); only an explicit false says so.
        + (p.webProduct === false ? ' <span class="kind">not a web item</span>' : '')
        + (p.linkedWooId && String(p.linkedWooId) !== String(wooId) ? linkedTag(p) : '')
        + '</td><td class="num">' + esc(p.availableLive == null ? '' : p.availableLive)
        + '</td><td class="act"><button class="btn small primary" type="button" data-i="' + i + '">Choose</button></td></tr>').join('')
        + missing.map(u => '<tr class="unavail"><td>' + esc(u.invNo == null ? '—' : u.invNo) + '</td><td colspan="4">' + esc(u.message) + ' <span class="sub">Try again in a moment.</span></td></tr>').join('');
      list.innerHTML = rows ? '<table><tr><th>Item</th><th>SKU</th><th>Description</th><th class="num">Avail.</th><th></th></tr>' + rows + '</table>' : '';
      list.querySelectorAll('button[data-i]').forEach(b => b.addEventListener('click', () => onPick(r.items[Number(b.dataset.i)])));
    } catch (e) { say(out, e.message, true); }
  };
  go.addEventListener('click', search);
  q.addEventListener('keydown', e => { if (e.key === 'Enter') search(); });
  close.addEventListener('click', () => { holder.innerHTML = ''; });
  q.focus();
}

/**
 * Link Woo item → POSitive item (a job). Asks first (in-page) when the item is not a web item or is
 * linked to another Woo item; a refusal is shown right there with "Unlink #58 and link" instead of
 * vanishing. `box` = where the refusal goes (next to the row); `after` re-renders once linked.
 */
async function linkItem(connectorId, wooId, product, exceptionId, out, after, box) {
  if (product.webProduct === false && !await ask({
    title: 'Not a web item',
    message: 'POSitive item ' + product.invNo + (product.sku ? ' (' + product.sku + ')' : '') + ' is not marked as a web item, so later price and stock changes are not sent to Woo for it.',
    detail: 'Mark it as a web item in POSitive to keep Woo up to date.',
    ok: 'Link anyway', cancel: 'Choose another item' })) return;
  let replace = false;
  if (product.linkedWooId && String(product.linkedWooId) !== String(wooId)) {
    if (!await ask({
      title: 'Already linked',
      message: (product.sku || 'POSitive item ' + product.invNo) + ' is already linked to Woo #' + product.linkedWooId + '. Unlink #' + product.linkedWooId + ' and link it to Woo item #' + wooId + ' instead?',
      detail: 'Woo #' + product.linkedWooId + ' stays in your store as it is, but its price and stock are no longer updated from POSitive.',
      ok: 'Unlink #' + product.linkedWooId + ' and link', cancel: 'Choose another item', danger: true })) return;
    replace = true;
  }
  return sendLink(connectorId, wooId, product, exceptionId, out, after, box, replace);
}

async function sendLink(connectorId, wooId, product, exceptionId, out, after, box, replace) {
  if (box) box.innerHTML = '';
  const startedAt = Date.now();
  try {
    const r = await post('/connectors/' + enc(connectorId) + '/review/link',
      { externalId: wooId, invNo: product.invNo, exceptionId: exceptionId || null, replace: !!replace });
    say(out, (replace ? 'Unlinking the old link, then linking' : 'Linking') + ' Woo item ' + wooId + ' to POSitive item ' + product.invNo + ' (job ' + r.jobId + ')…' + (r.note ? ' ' + r.note : ''));
    watchJob(connectorId, r.jobId, out, async job => {
      // The job ran; did POSitive or Woo refuse the link? (catalog_link_refused on this Woo id, raised by this run)
      const refused = job.status === 'done' ? await refusalFor(connectorId, wooId, startedAt) : null;
      if (refused) {
        say(out, '');
        showRefusal(box || out, refused.reason, connectorId, wooId, product, exceptionId, out, after, null);
        return;
      }
      if (job.status === 'done') banner('Linked Woo item ' + wooId + ' to POSitive item ' + product.invNo + (product.sku ? ' (' + product.sku + ')' : '') + '.', true);
      after && after();
    });
  } catch (e) {
    if (e.status === 409 && e.body.errorCode === 'link_conflict') {
      say(out, '');
      showRefusal(box || out, e.message, connectorId, wooId, product, exceptionId, out, after, e.body.conflict);
    } else say(out, e.message, true);
  }
}

async function refusalFor(connectorId, wooId, since) {
  try {
    const r = await api('/connectors/' + enc(connectorId) + '/exceptions?status=open&kind=catalog_link_refused');
    return r.items.find(x => x.externalRef === String(wooId) && new Date(x.updatedAt).getTime() >= since - 60000) || null;
  } catch (e) { return null; }
}

/** The refusal, inline, with the way out: unlink what is in the way and link (after a confirm). */
function showRefusal(box, message, connectorId, wooId, product, exceptionId, out, after, conflict) {
  const blocker = conflict ? (conflict.side === 'pos' ? '#' + conflict.linkedWooId : 'it from POSitive item ' + conflict.linkedInvNo) : 'the old link';
  const wrap = document.createElement('div');
  wrap.className = 'refusal';
  wrap.setAttribute('role', 'alert');
  const msg = document.createElement('div'); msg.className = 'note bad'; msg.textContent = 'Not linked: ' + message;
  const btns = document.createElement('div'); btns.className = 'btns';
  const unlink = document.createElement('button'); unlink.type = 'button'; unlink.className = 'btn small danger';
  unlink.textContent = 'Unlink ' + blocker + ' and link';
  const other = document.createElement('button'); other.type = 'button'; other.className = 'btn small'; other.textContent = 'Choose a different item';
  btns.append(unlink, other); wrap.append(msg, btns);
  if (box.classList && box.classList.contains('note')) { box.textContent = ''; box.after(wrap); } else { box.innerHTML = ''; box.append(wrap); }
  unlink.addEventListener('click', async () => {
    const ok = await ask({
      title: 'Unlink and link?',
      message: 'Unlink ' + blocker + ', then link Woo item #' + wooId + ' to POSitive item ' + product.invNo + (product.sku ? ' (' + product.sku + ')' : '') + '.',
      detail: conflict && conflict.side === 'pos'
        ? 'Woo #' + conflict.linkedWooId + ' stays in your store as it is (delete it in WooCommerce if it is not needed), but POSitive no longer updates its price and stock.'
        : 'The item that loses its link is no longer updated from POSitive.',
      ok: 'Unlink and link', danger: true });
    if (!ok) return;
    wrap.remove();
    sendLink(connectorId, wooId, product, exceptionId, out, after, box.classList && box.classList.contains('note') ? null : box, true);
  });
  other.addEventListener('click', () => { wrap.remove(); });
  unlink.focus();
}

// ---- match review -----------------------------------------------------------------------------------
async function viewReview(id) {
  setTabs(id, 'review');
  const [r, c] = await Promise.all([api('/connectors/' + enc(id) + '/review'), api('/connectors/' + enc(id))]);
  const sum = r.lastRun && r.lastRun.summary;
  const active = r.active.length ? r.active[0] : null;
  const w = ME.canWrite;
  const btn = (cls, act, label) => '<button class="btn small ' + cls + '" type="button" data-act="' + act + '">' + esc(label) + '</button> ';
  const byGroup = k => r.items.filter(x => x.group === k);
  const quiet = g => g.key === 'matrix' || g.key === 'no_sku'; // the group's text says it all
  const isParent = g => g.key.indexOf('parent:') === 0;

  // Every row says what it is by name and SKU, then its id (never a bare "Woo #24"); a variation
  // under its product shows its own attribute values ("Red"). The occurrence count is not shown here.
  const ids = x => [x.sku ? 'SKU ' + esc(x.sku) : null,
    x.wooId ? 'Woo #' + esc(x.wooId) : 'POSitive item ' + esc(x.invNo != null ? x.invNo : x.externalRef)].filter(Boolean).join(' · ');
  const title = (x, g) => {
    if (isParent(g)) return x.label || x.name || 'Variation #' + x.wooId;
    if (x.wooId) return x.label || x.name || 'Woo item #' + x.wooId;
    return x.name || 'POSitive item ' + (x.invNo != null ? x.invNo : x.externalRef);
  };
  // "No POSitive web item has SKU …" is what the group already says; other reasons are worth a line.
  const ownReason = (x, g) => !quiet(g) && !/^No POSitive web item has SKU/.test(x.reason || '');
  const itemRow = (x, g) => '<tr data-ex="' + esc(x.exceptionId) + '"' + (isParent(g) ? ' class="child"' : '') + '>'
      + (w ? '<td class="sel"><input type="checkbox" aria-label="select ' + esc(title(x, g)) + '" data-sel="' + esc(x.exceptionId) + '"></td>' : '')
      + '<td><b>' + esc(title(x, g)) + '</b> <span class="sub">' + ids(x) + '</span>'
      + (x.wooStatus ? ' <span class="kind">' + esc(x.wooStatus) + '</span>' : '')
      + (ownReason(x, g) ? '<div class="sub" style="margin:2px 0 0">' + esc(x.reason) + '</div>' : '')
      + '<div class="note rowout" role="status" style="margin:2px 0 0"></div><div class="pk"></div></td>'
      + '<td class="act">' + (w ? (x.canLink ? btn('primary', 'link', 'Link…') : '') + (x.canCreate ? btn('', 'create', 'Create in Woo') : '') + btn('', 'ignore', 'Ignore') : '') + '</td></tr>';
  const groupCard = g => {
    const items = byGroup(g.key);
    const parent = isParent(g);
    return '<div class="card" data-group="' + esc(g.key) + '"><h2>' + esc(g.title) + (parent ? '' : ' <span class="sub">(' + items.length + ')</span>') + '</h2>'
      + '<div class="sub">' + esc(g.explanation) + '</div>'
      + (w ? '<div class="btns" style="margin:0 0 6px">'
        + (parent && items.some(x => x.canLink) ? '<button class="btn small primary" type="button" data-parent-link title="Link the variations one by one">Link…</button>' : '')
        + '<button class="btn small" type="button" data-bulk="all">Ignore all ' + items.length + '</button>'
        + '<button class="btn small" type="button" data-bulk="sel">Ignore selected</button><span class="note" data-bulk-out></span></div>' : '')
      + '<div class="tscroll"><table>' + items.map(x => itemRow(x, g)).join('') + '</table></div></div>';
  };

  view('<div class="card"><h2>Match review</h2><div class="sub">Woo SKU = POSitive SKU or barcode (case-insensitive). Linked items get POSitive\'s price and stock. '
    + 'What is listed here is not an error — it is what the match could not pair on its own.</div>'
    + '<div class="tiles">' + tile('Linked items', esc(r.linkedItems), '') + tile('Last run', r.lastRun ? agoHtml(r.lastRun.completedAt) : 'never', r.lastRun ? r.lastRun.kind : '')
    + (sum ? tile('Paired / created', esc(sum.matched + ' / ' + sum.created), 'woo ' + sum.wooItems + ' · pos ' + sum.posItems) : '')
    + tile('To review', esc(r.needsReview), r.ignored.length + ' ignored') + '</div>'
    + (r.openExceptions ? '<div class="note">' + esc(plural(r.openExceptions, 'exception')) + ' need action (failed pushes or creates, refused links, orders): <a href="#/c/' + esc(enc(id)) + '/exceptions">Exceptions</a>.</div>' : '')
    + (active ? '<div class="note" role="status">Running: ' + esc(active.kind) + ' (job ' + esc(active.jobId) + ', ' + esc(active.status) + '). This page refreshes until it finishes.</div>' : '')
    + (w ? '<label class="check" style="margin-top:8px"><input type="checkbox" id="m-create" checked> also create POSitive web items Woo does not have (as ' + esc(c.publishNewAs) + ')</label>'
      + '<div class="btns"><button class="btn primary" id="b-match" type="button"' + (active ? ' disabled' : '') + '>Run the match</button></div>' : '')
    + (!c.enabled ? '<div class="note">The connector is disabled: queued jobs run once it is enabled.</div>' : '')
    + '<div class="note" id="m-out" role="status"></div></div>'
    + (r.groups.length ? r.groups.map(groupCard).join('') : '<div class="card"><h2>Nothing to review</h2><div class="note">' + (r.lastRun ? 'Every item the match saw is paired or ignored.' : 'Run the match to pair Woo products with POSitive items.') + '</div></div>')
    + '<div class="card" id="ignored-card"><h2>Ignored <span class="sub">(' + r.ignored.length + ')</span></h2><div class="sub">Not reported by the match again until un-ignored.</div>'
    + (r.ignored.length ? (w ? '<div class="btns" style="margin:0 0 6px"><button class="btn small" type="button" id="b-unig-sel">Un-ignore selected</button><span class="note" id="unig-out"></span></div>' : '')
      + '<div class="tscroll"><table>' + r.ignored.map(g => '<tr>' + (w ? '<td class="sel"><input type="checkbox" aria-label="select ' + esc(g.externalRef) + '" data-unsel="' + esc(g.externalRef) + '"></td>' : '')
        + '<td><b>' + esc(g.externalRef) + '</b> ' + kindHtml(g.kind)
        + '<div class="sub" style="margin:0">by ' + esc(g.ignoredBy) + ', ' + esc(when(g.ignoredAt)) + (g.note ? ' — ' + esc(g.note) : '') + '</div></td><td class="act">'
        + (w ? '<button class="btn small" type="button" data-unignore="' + esc(g.externalRef) + '">Un-ignore</button>' : '') + '</td></tr>').join('') + '</table></div>'
      : '<div class="note">Nothing ignored.</div>')
    + '</div>');

  const out = $('m-out');
  const reload = () => viewReview(id).catch(e => say(out, e.message, true));
  $('b-match') && $('b-match').addEventListener('click', async () => {
    try {
      const q = await post('/connectors/' + enc(id) + '/review/match', { createMissing: $('m-create').checked });
      say(out, 'The match is queued (job ' + q.jobId + ').' + (q.note ? ' ' + q.note : ''));
      watchJob(id, q.jobId, out, reload);
    } catch (e) { say(out, e.message, true); }
  });
  document.querySelectorAll('tr[data-ex]').forEach(tr => {
    const x = r.items.find(i => String(i.exceptionId) === tr.dataset.ex);
    const rowOut = tr.querySelector('.rowout'), pk = tr.querySelector('.pk');
    tr.querySelectorAll('button[data-act]').forEach(b => b.addEventListener('click', async () => {
      const act = b.dataset.act;
      try {
        if (act === 'link') {
          picker(pk, id, p => linkItem(id, x.wooId, p, x.exceptionId, rowOut, reload), x.wooId);
        } else if (act === 'create') {
          const q = await post('/connectors/' + enc(id) + '/review/create', { invNos: [x.invNo] });
          say(rowOut, 'Creating in Woo (job ' + q.jobId + ')…'); watchJob(id, q.jobId, rowOut, reload);
        } else if (act === 'ignore') {
          await post('/connectors/' + enc(id) + '/review/ignore', { externalRef: x.externalRef, kind: x.kind });
          reload();
        }
      } catch (e) { say(rowOut, e.message, true); }
    }));
  });
  // Bulk Ignore per group: the selected rows, or all of them (asked first).
  document.querySelectorAll('[data-group]').forEach(card => {
    const g = r.groups.find(x => x.key === card.dataset.group);
    const bulkOut = card.querySelector('[data-bulk-out]');
    // A variable product is never linked itself: Link… starts on its first variation (each links to its own item).
    const parentLink = card.querySelector('[data-parent-link]');
    parentLink && parentLink.addEventListener('click', () => {
      const first = card.querySelector('tr[data-ex] button[data-act="link"]');
      if (first) { first.closest('tr').scrollIntoView({ block: 'nearest' }); first.click(); }
    });
    card.querySelectorAll('button[data-bulk]').forEach(b => b.addEventListener('click', async () => {
      const all = b.dataset.bulk === 'all';
      const ids = all ? byGroup(g.key).map(x => String(x.exceptionId))
        : [...card.querySelectorAll('input[data-sel]:checked')].map(i => i.dataset.sel);
      const chosen = r.items.filter(x => ids.includes(String(x.exceptionId)));
      if (!chosen.length) { say(bulkOut, 'Tick the items to ignore first.', true); return; }
      if (!await ask({ title: 'Ignore ' + plural(chosen.length, 'item') + '?',
        message: 'The match stops listing ' + (chosen.length === 1 ? 'it' : 'them') + ' (' + g.title.toLowerCase() + '). Nothing changes in Woo or POSitive.',
        detail: 'You can un-ignore them at the bottom of this page.', ok: 'Ignore ' + chosen.length })) return;
      try {
        const res = await post('/connectors/' + enc(id) + '/review/ignore-many', { items: chosen.map(x => ({ externalRef: x.externalRef, kind: x.kind })) });
        banner(plural(res.ignored, 'item') + ' ignored.', true);
        reload();
      } catch (e) { say(bulkOut, e.message, true); }
    }));
  });
  document.querySelectorAll('button[data-unignore]').forEach(b => b.addEventListener('click', async () => {
    try { await post('/connectors/' + enc(id) + '/review/unignore', { externalRef: b.dataset.unignore }); reload(); }
    catch (e) { say(out, e.message, true); }
  }));
  $('b-unig-sel') && $('b-unig-sel').addEventListener('click', async () => {
    const refs = [...document.querySelectorAll('input[data-unsel]:checked')].map(i => i.dataset.unsel);
    if (!refs.length) { say($('unig-out'), 'Tick the items to un-ignore first.', true); return; }
    try {
      const res = await post('/connectors/' + enc(id) + '/review/unignore-many', { externalRefs: refs });
      banner(plural(res.unignored, 'item') + ' un-ignored: the next match reviews ' + (res.unignored === 1 ? 'it' : 'them') + ' again.', true);
      reload();
    } catch (e) { say($('unig-out'), e.message, true); }
  });
  if (active) every('review', () => { if (location.hash.endsWith('/review')) reload(); }, 5000);
  else stopTimer('review');
}

// ---- exceptions -------------------------------------------------------------------------------------
let exFilter = { status: 'open', kind: '' };

async function viewExceptions(id, exceptionId) {
  setTabs(id, 'exceptions');
  if (exceptionId) return viewException(id, exceptionId);
  const r = await api('/connectors/' + enc(id) + '/exceptions?status=' + enc(exFilter.status) + (exFilter.kind ? '&kind=' + enc(exFilter.kind) : ''));
  const kinds = [...new Set(r.items.map(x => x.kind).concat(exFilter.kind ? [exFilter.kind] : []))].sort();
  const counts = r.counts;
  view('<div class="card"><h2>Exceptions</h2><div class="sub">things that need you: an order that could not be imported, a payment, a failed push or create, a refused link — never adjusted silently</div>'
    + (r.needsReview ? '<div class="note">Not here: ' + esc(plural(r.needsReview, 'item')) + ' the match left for review (Woo products with no POSitive match and the like — normal, not errors) are on <a href="#/c/' + esc(enc(id)) + '/review">Match review</a>.</div>' : '')
    + '<div class="btns" style="margin-top:0">'
    + [['open', 'Open (' + counts.open + ')'], ['closed', 'Resolved / ignored (' + (counts.resolved + counts.retried + counts.ignored) + ')'], ['all', 'All']]
      .map(([k, l]) => '<button class="btn small' + (exFilter.status === k ? ' primary' : '') + '" type="button" data-status="' + k + '">' + esc(l) + '</button>').join('')
    + '<select class="inp" id="ex-kind" aria-label="kind" style="width:auto"><option value="">every kind</option>' + kinds.map(k => '<option value="' + esc(k) + '"' + (k === exFilter.kind ? ' selected' : '') + '>' + esc(KIND_TEXT[k] || k) + '</option>').join('') + '</select></div>'
    + '<div class="tscroll" style="margin-top:9px"><table><tr><th>What</th><th>Ref</th><th>Reason / outcome</th><th class="num">×</th><th>Updated</th><th>Status</th><th></th></tr>'
    + (r.items.length ? r.items.map(x => {
        const text = x.status !== 'open' && x.resolution ? x.resolution : x.reason;
        return '<tr class="pick" tabindex="0" data-id="' + esc(x.exceptionId) + '"><td>' + kindHtml(x.kind) + '</td><td>' + esc(x.externalRef) + '</td><td' + (x.status !== 'open' && !x.resolution ? ' class="sub"' : '') + '>'
          + esc(text.length > 160 ? text.slice(0, 160) + '…' : text) + '</td><td class="num">' + esc(x.occurrences) + '</td><td>' + agoHtml(x.updatedAt) + '</td><td>'
          + esc(statusText(x)) + (x.resolvedBy ? ' <span class="sub">by ' + esc(x.resolvedBy) + '</span>' : '') + '</td><td class="act">'
          + (ME.canWrite && x.status === 'open' && isHandledKind(x.kind) ? '<button class="btn small" type="button" data-handled="' + esc(x.exceptionId) + '">Mark as handled</button>' : '')
          + '</td></tr>';
      }).join('')
      : '<tr><td colspan="7" class="sub">none</td></tr>') + '</table></div><div class="note" id="ex-out" role="status"></div></div>');
  document.querySelectorAll('button[data-handled]').forEach(b => b.addEventListener('click', async e => {
    e.stopPropagation(); // the row itself opens the detail
    const x = r.items.find(i => String(i.exceptionId) === b.dataset.handled);
    try { if (await closeException(id, x)) route(); }
    catch (err) { say($('ex-out'), err.message, true); }
  }));
  document.querySelectorAll('button[data-handled]').forEach(b => b.addEventListener('keydown', e => e.stopPropagation()));
  document.querySelectorAll('button[data-status]').forEach(b => b.addEventListener('click', () => { exFilter.status = b.dataset.status; route(); }));
  $('ex-kind').addEventListener('change', e => { exFilter.kind = e.target.value; route(); });
  document.querySelectorAll('tr.pick').forEach(tr => {
    const open = () => { location.hash = '#/c/' + enc(id) + '/exceptions/' + tr.dataset.id; };
    tr.addEventListener('click', open);
    tr.addEventListener('keydown', e => { if (e.key === 'Enter') open(); });
  });
}

function prettyJson(text) {
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch (e) { return text; }
}
const prop = (o, name) => o == null ? undefined : (o[name] !== undefined ? o[name] : o[name.charAt(0).toUpperCase() + name.slice(1)]);

/** How a closed exception ended, in words — what the page leads with once it is no longer open. */
function outcomeText(x) {
  const by = x.resolvedBy ? ' by ' + x.resolvedBy : '';
  const at = x.resolvedAt ? ' on ' + when(x.resolvedAt) : '';
  if (x.resolution) return x.resolution;
  if (x.status === 'ignored') return (isHandledKind(x.kind) ? 'Marked as handled' : 'Ignored') + by + at + '.';
  if (x.status === 'retried') return 'Retried' + by + at + '. If the retry fails again it comes back as a new exception.';
  return 'Resolved' + by + at + '.';
}

async function viewException(id, exceptionId) {
  const r = await api('/connectors/' + enc(id) + '/exceptions/' + exceptionId);
  const x = r.exception, open = x.status === 'open', w = ME.canWrite && open;
  let lines = [];
  if (x.kind === 'order_unmatched_product' && x.snapshot) {
    try { lines = prop(JSON.parse(x.snapshot), 'unmatched') || []; } catch (e) { lines = []; }
  }
  const linesTable = lines.length ? '<h3>Lines with no POSitive item' + (open ? '' : ' (when it was raised)') + '</h3><div class="tscroll"><table><tr><th>Line</th><th>Woo item</th><th>SKU</th><th>Why</th><th></th></tr>'
      + lines.map((l, i) => '<tr><td>' + esc(prop(l, 'name') || '') + '</td><td>' + esc(prop(l, 'wooItemId') || '') + '</td><td>' + esc(prop(l, 'sku') || '') + '</td><td>'
        + esc(prop(l, 'problem') || '') + '<div class="pk" data-line="' + i + '"></div></td><td class="act">'
        + (w && prop(l, 'wooItemId') ? '<button class="btn small primary" type="button" data-link-line="' + i + '">Link item…</button>' : '') + '</td></tr>').join('')
      + '</table></div>' + (open ? '<div class="note">Linking an item re-imports this order once the link is made. With several lines, link them all (the last link\'s retry imports it).</div>' : '')
    : '';
  const problem = '<div class="note" style="font-size:13px' + (open ? ';color:var(--ink)' : '') + '">' + esc(x.reason) + '</div>'
    + (r.job ? '<div class="note">Job ' + esc(r.job.jobId) + ': ' + esc(r.job.kind) + ' ' + esc(r.job.resourceKey) + ' — ' + esc(r.job.status) + ', ' + esc(r.job.attempts) + ' tries'
        + (r.job.lastError ? ' · ' + esc(r.job.lastError) : '') + '</div>' : '')
    + linesTable
    + (x.snapshot ? '<h3>Snapshot</h3><pre>' + esc(prettyJson(x.snapshot)) + '</pre>' : '');

  view('<div class="card"><h2>' + kindHtml(x.kind) + ' ' + esc(x.externalRef) + '</h2>'
    + '<div class="sub">' + esc(statusText(x)) + (x.resolvedBy ? ' by ' + esc(x.resolvedBy) + ' ' + esc(when(x.resolvedAt)) : '') + ' · raised ' + esc(when(x.createdAt))
    + ' · last ' + esc(when(x.updatedAt)) + ' · ' + esc(x.occurrences) + ' time(s)' + (x.posRef ? ' · POSitive ' + esc(x.posRef) : '') + '</div>'
    // Closed: the outcome first; the original problem below, folded and greyed.
    + (open ? problem
      : '<div class="outcome ' + (x.status === 'ignored' ? 'ign' : 'ok') + '" role="status"><b>' + esc(x.status === 'ignored' ? (isHandledKind(x.kind) ? 'Handled' : 'Ignored') : x.status === 'retried' ? 'Retried' : 'Resolved') + '</b> — ' + esc(outcomeText(x)) + '</div>'
        + '<details class="orig"><summary>Original problem (raised ' + esc(when(x.createdAt)) + ')</summary>' + problem + '</details>')
    + '<div class="pk" id="pk-main"></div>'
    + (w ? '<div class="btns"><button class="btn primary" id="b-retry" type="button">Retry</button>'
      + (x.canLink && x.wooId ? '<button class="btn" id="b-link" type="button">Link item…</button>' : '')
      + (x.canCreate ? '<button class="btn" id="b-create" type="button">Create in Woo</button>' : '')
      + '<button class="btn" id="b-ignore" type="button">' + (isHandledKind(x.kind) ? 'Mark as handled' : 'Ignore') + '</button></div>' : '')
    + (open && isHandledKind(x.kind) ? '<div class="note">Fix the order in POSitive (cancel, refund or edit it to match WooCommerce), then <b>Mark as handled</b>. Nothing is changed in POSitive automatically.</div>' : '')
    + '<div class="note" id="x-out" role="status"></div><div class="btns"><a class="btn" href="#/c/' + esc(enc(id)) + '/exceptions">← Back to the list</a></div></div>');

  const out = $('x-out');
  const back = () => { location.hash = '#/c/' + enc(id) + '/exceptions'; };
  $('b-retry') && $('b-retry').addEventListener('click', async () => {
    try {
      const q = await post('/connectors/' + enc(id) + '/exceptions/' + exceptionId + '/retry');
      say(out, 'Re-queued as ' + q.kind + ' (job ' + q.jobId + '). If it fails again, it comes back as a new exception.');
      watchJob(id, q.jobId, out, () => setTimeout(route, 1500));
    } catch (e) { say(out, e.message, true); }
  });
  $('b-ignore') && $('b-ignore').addEventListener('click', async () => {
    try { if (await closeException(id, x)) back(); }
    catch (e) { say(out, e.message, true); }
  });
  $('b-link') && $('b-link').addEventListener('click', () =>
    picker($('pk-main'), id, p => linkItem(id, x.wooId, p, null, out, () => route()), x.wooId));
  $('b-create') && $('b-create').addEventListener('click', async () => {
    try {
      const q = await post('/connectors/' + enc(id) + '/review/create', { invNos: [x.invNo] });
      say(out, 'Creating in Woo (job ' + q.jobId + ')…'); watchJob(id, q.jobId, out, () => route());
    } catch (e) { say(out, e.message, true); }
  });
  document.querySelectorAll('button[data-link-line]').forEach(b => b.addEventListener('click', () => {
    const l = lines[Number(b.dataset.linkLine)];
    const holder = document.querySelector('.pk[data-line="' + b.dataset.linkLine + '"]');
    picker(holder, id, p => linkItem(id, Number(prop(l, 'wooItemId')), p, exceptionId, out, () => route()), Number(prop(l, 'wooItemId')));
  }));
}

// ---- start --------------------------------------------------------------------------------------------
(async () => {
  try {
    ME = await api('/me');
    $('who').textContent = ME.user + (ME.canWrite ? '' : ' · view only');
  } catch (e) {
    $('who').textContent = '—';
    view('<div class="card"><h2>Connectors are not available</h2><div class="note bad">' + esc(e.message) + '</div>'
      + '<div class="btns"><a class="btn" href="/ops">← Server console</a></div></div>');
    return;
  }
  window.addEventListener('hashchange', route);
  route();
})();
