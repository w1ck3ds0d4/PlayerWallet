const state = {
  projects: [],
  config: null,
  pollingRunId: null,
  /** Per-project flag: have we ever seen /health/ready succeed since the page loaded? */
  seenHealthyOnce: {},
  /** When the dashboard page was loaded; informs the "still starting up" grace period. */
  pageLoadedAt: Date.now(),
};

const STARTUP_GRACE_MS = 5 * 60 * 1000;

const $ = (sel) => document.querySelector(sel);

async function loadProjects() {
  const [projects, config] = await Promise.all([
    fetch('/api/projects').then(r => r.json()),
    fetch('/api/config').then(r => r.json()),
  ]);
  state.projects = projects;
  state.config = config;

  const overrideText = config.scenarioRpsOverrides && Object.keys(config.scenarioRpsOverrides).length
    ? ' (overrides: ' + Object.entries(config.scenarioRpsOverrides).map(([k, v]) => `${k}=${v}rps`).join(', ') + ')'
    : '';
  $('#config-summary').textContent =
    `${projects.length} projects | ${config.requestsPerSecond} rps default${overrideText} | ${config.warmUpSeconds}s warmup ${config.durationSeconds}s measure | http timeout ${config.httpTimeoutSeconds}s`;

  // Surface the reports root so the user knows where on-disk run folders live.
  const reportsLabel = $('#reports-root');
  if (reportsLabel && config.reportsRoot) {
    reportsLabel.textContent = `Run artifacts (summary.json + NBomber HTML/CSV/MD/TXT) saved under: ${config.reportsRoot}`;
  }

  // Seed the duration input with the server-configured default; user can override per run.
  const durationInput = $('#duration');
  if (durationInput) durationInput.value = config.durationSeconds;

  renderProjects();
  renderProjectCheckboxes();
}

function renderProjects() {
  const container = $('#projects');
  container.innerHTML = '';
  for (const p of state.projects) {
    const card = document.createElement('div');
    card.className = 'project-card';
    card.style.borderLeftColor = p.color;
    card.innerHTML = `
      <h3>${p.name} <span class="badge warn" data-health="${p.name}">starting up...</span></h3>
      <div class="url">${p.url}</div>
      <div class="muted" data-health-detail="${p.name}" style="margin-top:6px;font-size:11px;">Waiting for the AppHost to come up. First boot pulls Postgres/Kafka images and can take a minute.</div>
    `;
    container.appendChild(card);
  }
  refreshHealth();
}

function renderProjectCheckboxes() {
  const container = $('#project-checkboxes');
  container.innerHTML = '';
  for (const p of state.projects) {
    const label = document.createElement('label');
    label.innerHTML = `<input type="checkbox" value="${p.name}" checked> ${p.name}`;
    container.appendChild(label);
  }
}

async function refreshHealth() {
  await Promise.all(state.projects.map(async (p) => {
    const badge = document.querySelector(`[data-health="${p.name}"]`);
    const detail = document.querySelector(`[data-health-detail="${p.name}"]`);
    if (!badge || !detail) return;

    try {
      const resp = await fetch(`/api/health/${p.name}`);
      const data = await resp.json();
      const wasHealthyBefore = state.seenHealthyOnce[p.name] === true;
      const withinGrace = Date.now() - state.pageLoadedAt < STARTUP_GRACE_MS;

      if (data.healthy) {
        state.seenHealthyOnce[p.name] = true;
        badge.className = 'badge ok';
        badge.textContent = `up (${data.statusCode})`;
        detail.textContent = data.detail?.slice(0, 80) || '';
      } else if (!wasHealthyBefore && withinGrace) {
        badge.className = 'badge warn';
        badge.textContent = 'starting up...';
        detail.textContent = 'Waiting for the AppHost to come up. First boot pulls Postgres/Kafka images and can take a minute.';
      } else if (data.statusCode === 0) {
        badge.className = 'badge bad';
        badge.textContent = 'unreachable';
        detail.textContent = wasHealthyBefore
          ? 'AppHost stopped responding. Check its terminal for errors.'
          : 'AppHost has not become reachable yet. Verify Docker is running and the AppHost terminal shows no errors.';
      } else {
        badge.className = 'badge bad';
        badge.textContent = `down (${data.statusCode})`;
        detail.textContent = data.detail?.slice(0, 80) || '';
      }
    } catch (e) {
      const wasHealthyBefore = state.seenHealthyOnce[p.name] === true;
      const withinGrace = Date.now() - state.pageLoadedAt < STARTUP_GRACE_MS;
      if (!wasHealthyBefore && withinGrace) {
        badge.className = 'badge warn';
        badge.textContent = 'starting up...';
        detail.textContent = 'Dashboard API not reachable yet.';
      } else {
        badge.className = 'badge bad';
        badge.textContent = 'error';
        detail.textContent = e.message || '';
      }
    }
  }));
}

async function startRun() {
  const scenario = $('#scenario').value;
  const projects = Array.from(document.querySelectorAll('#project-checkboxes input:checked')).map(i => i.value);
  if (projects.length === 0) {
    setRunStatus('Pick at least one project.', 'fail');
    return;
  }

  const durationRaw = parseInt($('#duration').value, 10);
  const durationSeconds = Number.isFinite(durationRaw) ? durationRaw : null;
  if (durationSeconds !== null && (durationSeconds < 10 || durationSeconds > 600)) {
    setRunStatus('Duration must be between 10 and 600 seconds.', 'fail');
    return;
  }

  // RPS is optional: blank input -> null -> server uses configured default + per-scenario overrides.
  // When the user types a value it overrides both globally for this run.
  const rpsRaw = $('#rps').value.trim();
  const requestsPerSecond = rpsRaw === '' ? null : parseInt(rpsRaw, 10);
  if (requestsPerSecond !== null && (!Number.isFinite(requestsPerSecond) || requestsPerSecond < 10 || requestsPerSecond > 2000)) {
    setRunStatus('RPS must be between 10 and 2000 (or blank for default).', 'fail');
    return;
  }

  $('#run').disabled = true;
  const rpsLabel = requestsPerSecond ?? 'default';
  setRunStatus(`Starting ${scenario} (${durationSeconds ?? 'default'}s @ ${rpsLabel} rps) against ${projects.join(', ')}...`, 'progress');

  try {
    const resp = await fetch('/api/bench', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scenario, projects, durationSeconds, requestsPerSecond }),
    });
    if (!resp.ok) {
      const err = await resp.json();
      setRunStatus(`Failed to start: ${err.detail || err.title}`, 'fail');
      $('#run').disabled = false;
      return;
    }
    const { id } = await resp.json();
    state.pollingRunId = id;
    pollRun(id);
  } catch (e) {
    setRunStatus(`Start error: ${e.message}`, 'fail');
    $('#run').disabled = false;
  }
}

const TERMINAL_STATUSES = new Set(['Completed', 'Failed']);

function isTerminalStatus(status) {
  return TERMINAL_STATUSES.has(status) || status === 4 || status === 5;
}

async function pollRun(id) {
  try {
    while (true) {
      try {
        const resp = await fetch(`/api/bench/${id}`);
        if (!resp.ok) break;
        const run = await resp.json();
        renderLatestResult(run);
        const terminal = isTerminalStatus(run.status);
        const cssClass = terminal ? (run.status === 'Failed' || run.status === 5 ? 'fail' : 'done') : 'progress';
        setRunStatus(`${run.status} - ${run.statusDetail || ''}`, cssClass);
        if (terminal) {
          refreshHistory();
          break;
        }
      } catch (e) {
        setRunStatus(`Poll error: ${e.message}`, 'fail');
        break;
      }
      await new Promise(r => setTimeout(r, 1000));
    }
  } finally {
    $('#run').disabled = false;
  }
}

function renderLatestResult(run) {
  const container = $('#latest-result');
  if (!run.outcomes || run.outcomes.length === 0) {
    container.innerHTML = `<div class="muted">${run.status}: ${run.statusDetail || ''}</div>`;
    container.className = '';
    return;
  }

  const grid = document.createElement('div');
  grid.className = 'outcome-grid';

  for (const o of run.outcomes) {
    const project = state.projects.find(p => p.name === o.project);
    const color = project?.color || '#888';
    const card = document.createElement('div');
    card.className = 'outcome-card';
    card.style.borderLeftColor = color;
    card.innerHTML = `
      <h4>${o.project} <span class="muted" style="font-size:11px;">${o.scenario}</span></h4>
      <div class="stat"><span class="label">OK</span><span class="val">${o.okCount.toLocaleString()}</span></div>
      <div class="stat"><span class="label">FAIL</span><span class="val" style="color:${o.failCount > 0 ? 'var(--bad)' : 'var(--muted)'}">${o.failCount.toLocaleString()}</span></div>
      <div class="stat"><span class="label">RPS</span><span class="val">${o.avgRps.toFixed(1)}</span></div>
      <div class="stat"><span class="label">mean</span><span class="val">${o.meanMs.toFixed(2)} ms</span></div>
      <div class="stat"><span class="label">p50</span><span class="val">${o.p50Ms.toFixed(2)} ms</span></div>
      <div class="stat"><span class="label">p95</span><span class="val">${o.p95Ms.toFixed(2)} ms</span></div>
      <div class="stat"><span class="label">p99</span><span class="val">${o.p99Ms.toFixed(2)} ms</span></div>
      <div class="stat"><span class="label">stddev</span><span class="val">${o.stdDevMs.toFixed(2)}</span></div>
    `;
    grid.appendChild(card);
  }

  container.innerHTML = '';
  container.className = '';
  container.appendChild(grid);
}

function renderComparison(runs) {
  const tbody = $('#comparison-body');
  tbody.innerHTML = '';

  const comparable = runs
    .filter(r => r.status === 'Completed' && r.outcomes.length >= 2)
    .filter(r => r.outcomes.some(o => o.project === 'v1') && r.outcomes.some(o => o.project === 'v2'));

  if (comparable.length === 0) {
    tbody.innerHTML = '<tr><td colspan="14" class="muted" style="text-align:center;padding:24px;">No comparison runs yet. Run a benchmark with both v1 and v2 checked to populate this table.</td></tr>';
    return;
  }

  for (const r of comparable) {
    const v1 = r.outcomes.find(o => o.project === 'v1');
    const v2 = r.outcomes.find(o => o.project === 'v2');
    const dt = new Date(r.startedAt).toLocaleTimeString();

    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td>${dt}</td>
      <td>${r.scenario}</td>
      <td>${r.durationSeconds}s</td>
      <td>${r.requestsPerSecond}</td>
      <td>${v1.meanMs.toFixed(2)}</td>
      <td>${v2.meanMs.toFixed(2)}</td>
      ${deltaCell(v1.meanMs, v2.meanMs)}
      <td>${v1.p95Ms.toFixed(2)}</td>
      <td>${v2.p95Ms.toFixed(2)}</td>
      ${deltaCell(v1.p95Ms, v2.p95Ms)}
      <td>${v1.p99Ms.toFixed(2)}</td>
      <td>${v2.p99Ms.toFixed(2)}</td>
      ${deltaCell(v1.p99Ms, v2.p99Ms)}
      <td style="color:${v2.failCount > 0 ? 'var(--bad)' : 'var(--muted)'}">${v2.failCount.toLocaleString()}</td>
    `;
    tbody.appendChild(tr);
  }
}

function deltaCell(v1, v2) {
  if (!Number.isFinite(v1) || v1 <= 0) {
    return '<td class="delta-neutral">-</td>';
  }
  const pct = ((v2 - v1) / v1) * 100;
  const abs = Math.abs(pct);
  // ±2% threshold: smaller than that is measurement noise on a 30s bench, so neither side wins.
  // Arrow direction always reflects sign so even tiny differences show which way they lean.
  let cls;
  let arrow;
  if (abs < 2) {
    cls = 'delta-neutral';
    arrow = '·';
  } else if (pct < 0) {
    cls = 'delta-better';
    arrow = '▼';
  } else {
    cls = 'delta-worse';
    arrow = '▲';
  }
  const sign = pct > 0 ? '+' : '';
  return `<td class="${cls}">${arrow} ${sign}${pct.toFixed(0)}%</td>`;
}

async function refreshHistory() {
  const runs = await fetch('/api/bench').then(r => r.json());
  renderComparison(runs);
  const tbody = $('#history-body');
  tbody.innerHTML = '';
  for (const r of runs) {
    const tr = document.createElement('tr');
    const dt = new Date(r.startedAt).toLocaleTimeString();
    const sums = r.outcomes.map(o =>
      `${o.project}: ${o.meanMs.toFixed(1)} / ${o.p95Ms.toFixed(1)} / ${o.p99Ms.toFixed(1)}`
    ).join(' | ') || '-';
    const okTotal = r.outcomes.reduce((s, o) => s + o.okCount, 0);
    const failTotal = r.outcomes.reduce((s, o) => s + o.failCount, 0);
    const folderCell = r.folderPath
      ? `<span title="${r.folderPath}" data-copy="${r.folderPath}" style="cursor:copy;">${shortenPath(r.folderPath)}</span>`
      : '<span class="muted">-</span>';
    tr.innerHTML = `
      <td>${dt}</td>
      <td>${r.scenario}</td>
      <td>${r.projectNames.join(', ')}</td>
      <td>${r.status}</td>
      <td>${okTotal.toLocaleString()} / ${failTotal.toLocaleString()}</td>
      <td>${sums}</td>
      <td>${folderCell}</td>
    `;
    tr.style.cursor = 'pointer';
    tr.addEventListener('click', async () => {
      const run = await fetch(`/api/bench/${r.id}`).then(x => x.json());
      renderLatestResult(run);
    });
    tbody.appendChild(tr);
  }
}

function setRunStatus(text, cls) {
  const el = $('#run-status');
  el.textContent = text;
  el.className = `muted run-status ${cls || ''}`;
}

function shortenPath(p) {
  if (!p) return '';
  const parts = p.replace(/\\/g, '/').split('/');
  if (parts.length <= 2) return p;
  return `…/${parts.slice(-2).join('/')}`;
}

document.addEventListener('click', (e) => {
  const target = e.target;
  if (target instanceof HTMLElement && target.dataset.copy) {
    navigator.clipboard.writeText(target.dataset.copy).then(() => {
      const original = target.textContent;
      target.textContent = 'copied!';
      setTimeout(() => { target.textContent = original; }, 800);
    }).catch(() => {});
    e.stopPropagation();
  }
});

$('#run').addEventListener('click', startRun);

async function refreshDbStats() {
  const container = $('#db-stats-body');
  container.innerHTML = '<div class="muted">Loading...</div>';
  try {
    const results = await fetch('/api/db-stats').then(r => r.json());
    container.innerHTML = '';
    for (const r of results) {
      const card = document.createElement('div');
      card.className = 'project-card';
      const project = state.projects.find(p => p.name === r.project);
      card.style.borderLeftColor = project?.color || '#888';
      if (!r.ok) {
        card.innerHTML = `<h3>${r.project} <span class="badge bad">${r.statusCode || 'err'}</span></h3><div class="muted" style="font-size:12px;">${r.note}</div>`;
      } else {
        const tables = r.data.tables || [];
        const tableHtml = tables.map(t => `
          <div style="display:grid;grid-template-columns:auto 1fr;gap:4px 12px;font-size:12px;margin-bottom:10px;">
            <div style="font-weight:600;color:var(--accent);grid-column:1/3;">${t.tableName}</div>
            <div class="muted">size (total / table / index)</div><div class="val">${formatBytes(t.totalSizeBytes)} / ${formatBytes(t.tableSizeBytes)} / ${formatBytes(t.indexesSizeBytes)}</div>
            <div class="muted">live / dead tuples</div><div class="val">${t.liveTuples.toLocaleString()} / <span style="color:${t.deadTuples > t.liveTuples * 0.3 ? 'var(--bad)' : 'var(--text)'}">${t.deadTuples.toLocaleString()}</span></div>
            <div class="muted">inserts / updates / deletes</div><div class="val">${t.inserts.toLocaleString()} / ${t.updates.toLocaleString()} / ${t.deletes.toLocaleString()}</div>
            <div class="muted">HOT updates</div><div class="val">${t.hotUpdates.toLocaleString()} (${t.updates > 0 ? Math.round(100 * t.hotUpdates / t.updates) : 0}% of updates)</div>
            <div class="muted">autovacuum count</div><div class="val">${t.autovacuumCount} (last: ${formatTimestamp(t.lastAutovacuum)})</div>
            <div class="muted">manual vacuum count</div><div class="val">${t.vacuumCount} (last: ${formatTimestamp(t.lastVacuum)})</div>
          </div>
        `).join('');
        card.innerHTML = `<h3>${r.project} <span class="badge ok">db-stats</span></h3><div class="muted" style="font-size:11px;margin-bottom:8px;">Sampled at ${new Date(r.data.sampledAt).toLocaleTimeString()}</div>${tableHtml}`;
      }
      container.appendChild(card);
    }
  } catch (e) {
    container.innerHTML = `<div class="muted">Failed to load: ${e.message}</div>`;
  }
}

function formatBytes(b) {
  if (!b || b < 1024) return `${b || 0} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(1)} MB`;
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

function formatTimestamp(ts) {
  if (!ts) return 'never';
  const d = new Date(ts);
  const ageMs = Date.now() - d.getTime();
  if (ageMs < 60_000) return `${Math.round(ageMs / 1000)}s ago`;
  if (ageMs < 3600_000) return `${Math.round(ageMs / 60_000)}m ago`;
  return d.toLocaleTimeString();
}

$('#refresh-db-stats').addEventListener('click', refreshDbStats);

$('#reset-outboxes').addEventListener('click', async () => {
  const btn = $('#reset-outboxes');
  btn.disabled = true;
  const original = btn.textContent;
  btn.textContent = 'Resetting…';
  try {
    const resp = await fetch('/api/reset-outboxes', { method: 'POST' });
    const results = await resp.json();
    const summary = results.map(r => `${r.project}: ${r.ok ? 'cleared' : (r.statusCode || 'err') + ' (' + r.note + ')'}`).join(' | ');
    setRunStatus(`Reset outboxes — ${summary}`, results.every(r => r.ok) ? 'done' : 'progress');
  } catch (e) {
    setRunStatus(`Reset failed: ${e.message}`, 'fail');
  } finally {
    btn.textContent = original;
    btn.disabled = false;
  }
});

loadProjects().then(() => {
  refreshHistory();
  refreshDbStats();
  setInterval(refreshHealth, 5000);
  setInterval(refreshDbStats, 30_000);
});
