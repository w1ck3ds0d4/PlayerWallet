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

  $('#run').disabled = true;
  setRunStatus(`Starting ${scenario} (${durationSeconds ?? 'default'}s) against ${projects.join(', ')}...`, 'progress');

  try {
    const resp = await fetch('/api/bench', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scenario, projects, durationSeconds }),
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

async function refreshHistory() {
  const runs = await fetch('/api/bench').then(r => r.json());
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

loadProjects().then(() => {
  refreshHistory();
  setInterval(refreshHealth, 5000);
});
