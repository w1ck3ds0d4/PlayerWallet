const state = {
  projects: [],
  config: null,
  pollingRunId: null,
};

const $ = (sel) => document.querySelector(sel);

async function loadProjects() {
  const [projects, config] = await Promise.all([
    fetch('/api/projects').then(r => r.json()),
    fetch('/api/config').then(r => r.json()),
  ]);
  state.projects = projects;
  state.config = config;

  $('#config-summary').textContent =
    `${projects.length} projects | ${config.requestsPerSecond} rps per project ${config.warmUpSeconds}s warmup ${config.durationSeconds}s measure`;

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
      <h3>${p.name} <span class="badge warn" data-health="${p.name}">checking...</span></h3>
      <div class="url">${p.url}</div>
      <div class="muted" data-health-detail="${p.name}" style="margin-top:6px;font-size:11px;"></div>
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
    try {
      const resp = await fetch(`/api/health/${p.name}`);
      const data = await resp.json();
      const badge = document.querySelector(`[data-health="${p.name}"]`);
      const detail = document.querySelector(`[data-health-detail="${p.name}"]`);
      if (data.healthy) {
        badge.className = 'badge ok';
        badge.textContent = `up (${data.statusCode})`;
      } else if (data.statusCode === 0) {
        badge.className = 'badge bad';
        badge.textContent = 'unreachable';
      } else {
        badge.className = 'badge bad';
        badge.textContent = `down (${data.statusCode})`;
      }
      detail.textContent = data.detail?.slice(0, 80) || '';
    } catch (e) {
      const badge = document.querySelector(`[data-health="${p.name}"]`);
      badge.className = 'badge bad';
      badge.textContent = 'error';
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

  $('#run').disabled = true;
  setRunStatus(`Starting ${scenario} against ${projects.join(', ')}...`, 'progress');

  try {
    const resp = await fetch('/api/bench', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scenario, projects }),
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

async function pollRun(id) {
  while (true) {
    try {
      const resp = await fetch(`/api/bench/${id}`);
      if (!resp.ok) break;
      const run = await resp.json();
      renderLatestResult(run);
      setRunStatus(`${run.status} - ${run.statusDetail || ''}`, run.status === 'Completed' ? 'done' : run.status === 'Failed' ? 'fail' : 'progress');
      if (run.status === 'Completed' || run.status === 'Failed') {
        $('#run').disabled = false;
        refreshHistory();
        break;
      }
    } catch (e) {
      setRunStatus(`Poll error: ${e.message}`, 'fail');
      $('#run').disabled = false;
      break;
    }
    await new Promise(r => setTimeout(r, 1000));
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
    tr.innerHTML = `
      <td>${dt}</td>
      <td>${r.scenario}</td>
      <td>${r.projectNames.join(', ')}</td>
      <td>${r.status}</td>
      <td>${okTotal.toLocaleString()} / ${failTotal.toLocaleString()}</td>
      <td>${sums}</td>
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

$('#run').addEventListener('click', startRun);

loadProjects().then(() => {
  refreshHistory();
  setInterval(refreshHealth, 5000);
});
