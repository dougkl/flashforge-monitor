'use strict';
const $ = id => document.getElementById(id);
const fmt = (v, d = 1) => v == null ? '–' : Number(v).toFixed(d);
const clock = t => t ? new Date(t).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : '';
const dur = s => s == null ? '–' : s >= 3600 ? `${Math.floor(s / 3600)}h ${Math.floor(s % 3600 / 60)}m` : `${Math.floor(s / 60)}m ${s % 60}s`;
let last = {}, lan = false, step = 10, lastDetail = null, thumbFor = null;

// ---------- helpers ----------
function toast(msg, err = false) {
  const t = $('toast'); t.textContent = msg; t.className = 'show' + (err ? ' err' : '');
  clearTimeout(toast.h); toast.h = setTimeout(() => t.className = '', 3500);
}
async function api(path, body, method = 'POST') {
  try {
    const r = await fetch(path, {
      method, headers: body ? { 'Content-Type': 'application/json' } : {},
      body: body ? JSON.stringify(body) : undefined,
    });
    const j = await r.json().catch(() => ({ ok: r.ok, message: r.statusText }));
    if (j && j.ok === false) toast(j.message || 'Failed', true);
    else if (j && j.message) toast(j.message);
    return j;
  } catch (e) { toast(String(e), true); return { ok: false }; }
}
const getJson = async p => (await fetch(p)).json();

// ---------- config ----------
async function loadConfig() {
  try {
    const c = await getJson('api/config');
    lan = c.lanMode;
    document.body.classList.toggle('lan', lan);
    $('setupBanner').hidden = c.configured;
    $('ver').textContent = c.version ? 'v' + c.version : '';
  } catch {}
}

// ---------- status ----------
async function tick() {
  let s;
  try { s = await getJson('api/status'); } catch {
    $('online').textContent = 'monitor unreachable'; $('online').className = 'pill off'; return;
  }
  last = s;
  $('title').textContent = s.model || 'FlashForge Monitor';
  $('online').textContent = s.online ? 'online' : 'offline';
  $('online').className = 'pill ' + (s.online ? 'on' : 'off');
  $('state').textContent = s.state;
  $('state').className = 'pill ' + ({ printing: 'on', paused: 'warm', completed: 'on' }[s.state] || '');
  $('activity').hidden = !s.activity;
  $('activity').textContent = s.activity ? `${s.activity}${s.activityPercent != null ? ' ' + s.activityPercent + '%' : ''}` : '';
  $('meta').textContent = s.online
    ? `fw ${s.firmware ?? '?'} · updated ${new Date(s.updatedAt).toLocaleTimeString()}`
    : (s.error || '');
  $('meta').title = s.serial ? 'Serial ' + s.serial : '';

  const active = s.state === 'printing' || s.state === 'paused';
  $('pct').textContent = active ? (s.progress != null ? s.progress + '%' : '…')
    : s.state === 'completed' ? 'Done' : s.state === 'offline' ? '–' : 'Idle';
  $('barfill').style.width = (active ? s.progress ?? 0 : s.state === 'completed' ? 100 : 0) + '%';
  $('layer').textContent = active && s.layerTotal ? `Layer ${s.layer} / ${s.layerTotal}` : '';
  $('file').textContent = (active || s.state === 'completed') && s.currentFile ? s.currentFile : 'No active print';
  const bits = [];
  if (s.printStartedAt) bits.push('started ' + clock(s.printStartedAt));
  if (s.estimatedFinish) bits.push('ETA ~' + clock(s.estimatedFinish));
  $('eta').textContent = bits.join(' · ');
  $('ipBtn').textContent = s.host ? s.host : 'set printer IP';
  renderJob(s);
  document.querySelector('[data-job=pause]').disabled = s.state !== 'printing';
  document.querySelector('[data-job=resume]').disabled = s.state !== 'paused';
  document.querySelector('[data-job=cancel]').disabled = !active;

  $('lightBtn').textContent = s.ledOn ? '💡 Light off' : '💡 Light on';
  $('noz').textContent = fmt(s.nozzleTemp) + '°';
  $('nozT').textContent = s.nozzleTarget ? `target ${fmt(s.nozzleTarget, 0)}°` : 'heater off';
  $('bed').textContent = fmt(s.bedTemp) + '°';
  $('bedT').textContent = s.bedTarget ? `target ${fmt(s.bedTarget, 0)}°` : 'heater off';
  $('pos').textContent = s.x != null ? `X ${fmt(s.x, 1)} · Y ${fmt(s.y, 1)} · Z ${fmt(s.z, 2)}` : '';
  document.querySelectorAll('[data-jog]').forEach(b => {
    b.disabled = active && !b.dataset.jog.startsWith('Z');
  });
}

// ---------- LAN-mode details ----------
const detailLabels = {
  status: 'Status', printProgress: ['Progress', v => Math.round(v * 100) + '%'], printLayer: 'Layer', targetPrintLayer: 'Total layers',
  printDuration: ['Print time', dur], estimatedTime: ['Time left', dur], printSpeedAdjust: ['Speed', v => v + '%'],
  currentPrintSpeed: ['Head speed', v => v + ' mm/s'], rightTemp: ['Nozzle', v => fmt(v) + '°'], rightTargetTemp: ['Nozzle target', v => v + '°'],
  platTemp: ['Bed', v => fmt(v) + '°'], platTargetTemp: ['Bed target', v => v + '°'], chamberTemp: ['Chamber', v => fmt(v) + '°'],
  chamberTargetTemp: ['Chamber target', v => v + '°'], coolingFanSpeed: ['Part fan', v => v + '%'], chamberFanSpeed: ['Chamber fan', v => v + '%'],
  internalFanStatus: 'Internal filter', externalFanStatus: 'External filter', doorStatus: 'Door', lightStatus: 'Light',
  zAxisCompensation: ['Z offset', v => fmt(v, 3) + ' mm'], fillAmount: ['Infill', v => v + '%'],
  estimatedRightWeight: ['Filament (job)', v => fmt(v) + ' g'], estimatedRightLen: ['Filament length', v => fmt(v / 1000, 2) + ' m'],
  rightFilamentType: 'Filament', hasRightFilament: 'Filament loaded',
  cumulativePrintTime: ['Lifetime print time', v => dur(v * 60)], cumulativeFilament: ['Lifetime filament', v => fmt(v, 1) + ' m'],
  remainingDiskSpace: ['Free disk', v => fmt(v, 2) + ' GB'], nozzleModel: 'Nozzle', nozzleStyle: 'Nozzle style', measure: 'Build volume',
  camera: 'Camera', cameraStreamUrl: 'Camera URL', ipAddr: 'IP', macAddr: 'MAC', firmwareVersion: 'Firmware', name: 'Name',
  location: 'Location', errorCode: 'Error code', autoShutdown: 'Auto shutdown', tvoc: 'TVOC', printFileName: 'File',
};
async function loadDetail() {
  if (!lan) return;
  const r = await getJson('api/detail').catch(() => null);
  const box = $('detail');
  if (!r || r.code !== 0 || !r.detail) {
    box.innerHTML = ''; const d = document.createElement('div');
    d.textContent = (r && r.message) || 'No data'; box.append(d); return;
  }
  const d = r.detail; lastDetail = d; box.innerHTML = '';
  const shown = new Set();
  const add = (k, label, f) => {
    if (d[k] === undefined || d[k] === null || d[k] === '') return;
    shown.add(k);
    const row = document.createElement('div');
    const a = document.createElement('span'); a.textContent = label;
    const b = document.createElement('span');
    const v = d[k]; b.textContent = f ? f(v) : typeof v === 'object' ? JSON.stringify(v) : String(v); b.title = b.textContent;
    row.append(a, b); box.append(row);
  };
  for (const [k, spec] of Object.entries(detailLabels)) Array.isArray(spec) ? add(k, spec[0], spec[1]) : add(k, spec);
  for (const k of Object.keys(d).sort()) if (!shown.has(k) && typeof d[k] !== 'object') add(k, k);
}

// ---------- controls ----------
document.querySelectorAll('[data-job]').forEach(b => b.onclick = () => {
  const a = b.dataset.job;
  if (a === 'cancel' && !confirm('Cancel the current print?')) return;
  api('api/job/' + a);
});
document.querySelectorAll('[data-ctl]').forEach(b => b.onclick = () => api('api/control/' + b.dataset.ctl));
document.querySelectorAll('[data-lan]').forEach(b => b.onclick = async () => {
  await api('api/lan/' + b.dataset.lan);
  if (b.dataset.lan.startsWith('camera')) setTimeout(startCamera, 1500);
});
$('lightBtn').onclick = () => api('api/control/light/' + (last.ledOn ? 'off' : 'on'));
$('estop').onclick = () => {
  if (confirm('EMERGENCY STOP: halts everything immediately. You will need to power-cycle the printer. Continue?'))
    api('api/control/estop');
};
document.querySelectorAll('[data-set]').forEach(b => b.onclick = () => {
  const which = b.dataset.set, v = parseInt($(which === 'nozzle' ? 'nozSet' : 'bedSet').value, 10);
  if (isNaN(v)) return toast('Enter a temperature', true);
  api('api/control/temp', { [which]: v });
});
document.querySelectorAll('[data-off]').forEach(b => b.onclick = () => api('api/control/temp', { [b.dataset.off]: 0 }));
document.querySelectorAll('[data-pre]').forEach(b => b.onclick = () => {
  const [n, bed] = b.dataset.pre.split(',').map(Number);
  api('api/control/temp', { nozzle: n, bed });
});
for (const [id, url] of [['speed', 'speed'], ['flow', 'flow'], ['fan', 'fan']]) {
  const el = $(id), out = $(id + 'Out');
  el.oninput = () => out.textContent = el.value + '%';
  el.onchange = () => api('api/control/' + url, { percent: +el.value });
}
document.querySelectorAll('#step button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#step button').forEach(x => x.classList.remove('on'));
  b.classList.add('on'); step = +b.dataset.step;
});
document.querySelectorAll('[data-jog]').forEach(b => b.onclick = () => {
  const [axis, dir] = b.dataset.jog.split(',');
  const dist = axis === 'E' ? 5 * +dir : step * +dir;
  api('api/control/jog', { axis, distance: dist });
});

// ---------- camera ----------
function startCamera() {
  const img = $('camimg');
  img.onload = () => { img.hidden = false; $('camMsg').hidden = true; };
  img.onerror = () => { img.hidden = true; $('camMsg').hidden = false; };
  img.src = 'camera?t=' + Date.now();
}

// ---------- files ----------
async function loadFiles() {
  const ul = $('files');
  ul.innerHTML = '<li class="sub">Loading…</li>';
  const r = await getJson('api/files').catch(e => ({ ok: false, message: String(e) }));
  if (!Array.isArray(r)) { ul.innerHTML = ''; const li = document.createElement('li'); li.className = 'sub'; li.textContent = r.message || 'Could not read files'; ul.append(li); return; }
  ul.innerHTML = '';
  if (!r.length) { ul.innerHTML = '<li class="sub">No files</li>'; return; }
  for (const n of r) {
    const li = document.createElement('li');
    const img = document.createElement('img');
    img.loading = 'lazy'; img.src = 'api/thumb?name=' + encodeURIComponent(n);
    img.onerror = () => img.style.visibility = 'hidden';
    const span = document.createElement('span'); span.className = 'name'; span.textContent = n; span.title = n;
    const btn = document.createElement('button'); btn.textContent = 'Print';
    btn.onclick = () => confirm(`Start printing ${n}?`) && api('api/files/print', { fileName: n });
    li.append(img, span, btn); ul.append(li);
  }
}
$('refreshFiles').onclick = loadFiles;

$('upfile').onchange = () => {
  const f = $('upfile').files[0];
  $('upname').textContent = f ? `${f.name} (${(f.size / 1048576).toFixed(1)} MB)` : 'nothing selected';
  $('upbtn').disabled = !f;
};
$('upbtn').onclick = () => {
  const f = $('upfile').files[0]; if (!f) return;
  const fd = new FormData();
  fd.append('file', f); fd.append('printNow', $('printNow').checked);
  const xhr = new XMLHttpRequest();
  xhr.open('POST', 'api/files/upload');
  xhr.upload.onprogress = e => {
    if (!e.lengthComputable) return;
    const p = Math.round(e.loaded / e.total * 50); // first half: browser -> app
    $('upbar').style.width = p + '%';
    $('upstatus').textContent = p < 50 ? `Sending to monitor ${p * 2}%` : 'Sending to printer…';
  };
  const poll = setInterval(() => {
    if (last.activityPercent != null) $('upbar').style.width = (50 + last.activityPercent / 2) + '%';
  }, 1000);
  xhr.onloadend = () => {
    clearInterval(poll);
    let r; try { r = JSON.parse(xhr.responseText); } catch { r = { ok: false, message: xhr.statusText || 'Upload failed' }; }
    $('upbar').style.width = r.ok ? '100%' : '0';
    $('upstatus').textContent = r.message || (r.ok ? 'Done' : 'Failed');
    toast(r.message || 'Upload finished', !r.ok);
    $('upbtn').disabled = false; loadFiles();
  };
  $('upbtn').disabled = true; $('upbar').style.width = '0';
  xhr.send(fd);
};

// ---------- history ----------
async function loadHistory() {
  const h = await getJson('api/history').catch(() => []);
  const tb = $('history'); tb.innerHTML = '';
  if (!h.length) { tb.innerHTML = '<tr><td colspan="4" class="sub">No prints recorded yet</td></tr>'; return; }
  for (const e of h.slice(0, 50)) {
    const tr = document.createElement('tr');
    const cells = [e.file, new Date(e.endedAt).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }),
      e.durationSeconds != null ? (e.startTimeExact ? '' : '≥') + dur(e.durationSeconds) : '–',
      e.result === 'finished' ? 'finished' : `stopped ${e.lastProgress ?? '?'}%`];
    cells.forEach((c, i) => { const td = document.createElement('td'); td.textContent = c; td.title = c; if (i === 3) td.className = e.result; tr.append(td); });
    tb.append(tr);
  }
}
$('clearHistory').onclick = async () => {
  if (!confirm('Clear the print history?')) return;
  await fetch('api/history', { method: 'DELETE' }); loadHistory();
};

// ---------- console ----------
function log(line, cls) {
  const c = $('console'), div = document.createElement('div');
  if (cls) div.className = cls; div.textContent = line; c.append(div); c.scrollTop = c.scrollHeight;
}
async function sendCmd(cmd) {
  log('> ' + cmd, 'cmd');
  try {
    const r = await (await fetch('api/console', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ command: cmd }) })).json();
    log(r.message || (r.ok ? 'ok' : 'failed'));
  } catch (e) { log(String(e)); }
}
$('consoleForm').onsubmit = e => { e.preventDefault(); const v = $('consoleIn').value.trim(); if (v) { sendCmd(v); $('consoleIn').value = ''; } };
document.querySelectorAll('[data-cmd]').forEach(b => b.onclick = () => sendCmd(b.dataset.cmd));

// ---------- chart ----------
async function chart() {
  const h = await getJson('api/temps').catch(() => []);
  const svg = $('chart');
  if (h.length < 2) { svg.innerHTML = ''; return; }
  const max = Math.max(60, ...h.map(p => Math.max(p.nozzle, p.bed))) * 1.1;
  const W = 600, H = 160, n = h.length - 1;
  const path = key => h.map((p, i) => `${i ? 'L' : 'M'}${(i / n * W).toFixed(1)},${(H - p[key] / max * H).toFixed(1)}`).join('');
  const grid = [50, 100, 150, 200, 250].filter(v => v < max)
    .map(v => `<line x1="0" x2="${W}" y1="${H - v / max * H}" y2="${H - v / max * H}" stroke="#2a313b"/>` +
      `<text x="4" y="${H - v / max * H - 3}" fill="#7d8590" font-size="10">${v}°</text>`).join('');
  svg.innerHTML = grid +
    `<path d="${path('nozzle')}" fill="none" stroke="#b07a4f" stroke-width="2" vector-effect="non-scaling-stroke"/>` +
    `<path d="${path('bed')}" fill="none" stroke="#4f8f7a" stroke-width="2" vector-effect="non-scaling-stroke"/>`;
}

$('testNotify').onclick = () => api('api/notify/test');

// ---------- job details ----------
// Slicer time often sits in the file name, e.g. "Boat_PLA_14m3s.gcode" or "part_1h2m.3mf".
function slicerEstimate(name) {
  const m = /(?:^|[_\- ])(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?(?=\.[^.]+$)/i.exec(name || '');
  if (!m || !(m[1] || m[2] || m[3] || m[4])) return null;
  return (+(m[1] || 0)) * 86400 + (+(m[2] || 0)) * 3600 + (+(m[3] || 0)) * 60 + (+(m[4] || 0));
}
function renderJob(s) {
  const active = s.state === 'printing' || s.state === 'paused' || s.state === 'completed';
  const file = active ? s.currentFile : null;

  // thumbnail of the file being printed (fetched once per file)
  const img = $('jobThumb');
  if (file !== thumbFor) {
    thumbFor = file;
    img.hidden = true;
    if (file) {
      img.onload = () => img.hidden = false;
      img.onerror = () => img.hidden = true;
      img.src = 'api/thumb?name=' + encodeURIComponent(file);
    }
  }

  const rows = [];
  if (active) {
    const now = Date.now();
    if (s.printStartedAt) rows.push(['Elapsed', dur(Math.round((now - new Date(s.printStartedAt)) / 1000))]);
    const d = lan ? lastDetail : null;
    if (d) {
      if (d.printDuration != null) rows.push(['Print time', dur(d.printDuration)]);
      if (d.estimatedTime != null) {
        rows.push(['Time left', dur(Math.round(d.estimatedTime))]);
        rows.push(['Finishes', clock(now + d.estimatedTime * 1000)]);
      }
      if (d.estimatedRightWeight != null) rows.push(['Filament', fmt(d.estimatedRightWeight) + ' g']);
      if (d.estimatedRightLen != null) rows.push(['Length', fmt(d.estimatedRightLen / 1000, 2) + ' m']);
      if (d.currentPrintSpeed != null) rows.push(['Head speed', d.currentPrintSpeed + ' mm/s']);
      if (d.printSpeedAdjust != null) rows.push(['Speed', d.printSpeedAdjust + '%']);
      if (d.coolingFanSpeed != null) rows.push(['Part fan', d.coolingFanSpeed + '%']);
      if (d.chamberTemp != null) rows.push(['Chamber', fmt(d.chamberTemp) + '°']);
      if (d.zAxisCompensation != null) rows.push(['Z offset', fmt(d.zAxisCompensation, 3) + ' mm']);
      if (d.rightFilamentType) rows.push(['Material', d.rightFilamentType]);
    }
    const est = slicerEstimate(file);
    if (est) {
      rows.push(['Slicer estimate', dur(est)]);
      if (!d && s.printStartedAt) rows.push(['Finishes (slicer)', clock(new Date(s.printStartedAt).getTime() + est * 1000)]);
    }
    if (s.state === 'printing' && s.layer === 0 && (s.moveMode === 'HOMING' || (s.progress ?? 0) === 0))
      rows.push(['Phase', s.moveMode === 'HOMING' ? 'homing' : 'preparing (heating / leveling)']);
  }
  const box = $('jobInfo'); box.innerHTML = '';
  for (const [k, v] of rows) {
    const row = document.createElement('div');
    const a = document.createElement('span'); a.textContent = k;
    const b = document.createElement('span'); b.textContent = v;
    row.append(a, b); box.append(row);
  }
}

// ---------- settings ----------
const EVENTS = ['started', 'finished', 'cancelled', 'paused', 'resumed', 'offline', 'online'];
function hookRow(h = { type: 'ntfy', url: '', events: [] }) {
  const row = document.createElement('div'); row.className = 'hook';
  const sel = document.createElement('select');
  for (const t of ['ntfy', 'discord', 'json']) { const o = document.createElement('option'); o.value = t; o.textContent = t; sel.append(o); }
  sel.value = (h.type || 'json').toLowerCase();
  const url = document.createElement('input'); url.placeholder = 'https://ntfy.sh/your-topic'; url.value = h.url || '';
  url.dataset.events = (h.events || []).join(',');
  const del = document.createElement('button'); del.type = 'button'; del.textContent = '✕'; del.onclick = () => row.remove();
  row.append(sel, url, del);
  return row;
}
async function openSettings() {
  const s = await getJson('api/settings');
  $('sHost').value = s.host || '';
  $('sCode').value = ''; $('sCode').placeholder = s.hasCheckCode ? '•••••• saved (leave empty to keep)' : 'not set';
  $('sPoll').value = s.pollSeconds;
  $('sUser').value = s.username || 'admin';
  $('sPass').value = '';
  $('sPassState').textContent = s.hasPassword ? 'A password is set. Leave empty to keep it.' : 'No password: anyone on your network can open this page.';
  $('sHooks').innerHTML = '';
  for (const h of s.webhooks || []) $('sHooks').append(hookRow(h));
  $('sFile').textContent = 'Saved to ' + s.settingsFile;
  $('sFound').innerHTML = '';
  $('sTestResult').textContent = 'Find it on the printer screen: Settings → Network. Give it a fixed IP in your router.';
  $('sTestResult').className = 'sub';
  $('settings').showModal();
}
$('settingsBtn').onclick = openSettings;
$('ipBtn').onclick = openSettings;
$('setupLink').onclick = openSettings;
$('sAddHook').onclick = () => $('sHooks').append(hookRow());
$('sClearCode').onclick = async () => {
  await api('api/settings', { checkCode: '' }, 'PUT');
  $('sCode').placeholder = 'not set'; loadConfig();
};
$('sTest').onclick = async () => {
  const host = $('sHost').value.trim(); if (!host) return;
  $('sTestResult').textContent = 'Testing…'; $('sTestResult').className = 'sub';
  const r = await (await fetch('api/settings/test', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ host }) })).json();
  $('sTestResult').textContent = r.message; $('sTestResult').className = 'sub ' + (r.ok ? 'ok' : 'bad');
};
$('sFind').onclick = async () => {
  const ul = $('sFound'); ul.innerHTML = '<li class="sub">Searching the local network…</li>';
  const list = await (await fetch('api/discover', { method: 'POST' })).json().catch(() => []);
  ul.innerHTML = '';
  if (!list.length) {
    const li = document.createElement('li'); li.className = 'sub';
    li.textContent = 'No printers answered. Discovery only works on the same network/VLAN; type the IP instead.';
    ul.append(li); return;
  }
  for (const p of list) {
    const li = document.createElement('li');
    const t = document.createElement('span'); t.textContent = `${p.name || p.model} · ${p.model} · ${p.ip} · ${p.status}`;
    const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Use';
    b.onclick = () => { $('sHost').value = p.ip; ul.innerHTML = ''; };
    li.append(t, b); ul.append(li);
  }
};
$('settingsForm').onsubmit = async e => {
  if (e.submitter && e.submitter.value === 'cancel') return; // Close
  e.preventDefault();
  const body = {
    host: $('sHost').value.trim(),
    pollSeconds: +$('sPoll').value || 3,
    username: $('sUser').value.trim() || 'admin',
    webhooks: [...$('sHooks').querySelectorAll('.hook')].map(r => ({
      type: r.querySelector('select').value,
      url: r.querySelector('input').value.trim(),
      events: (r.querySelector('input').dataset.events || '').split(',').filter(Boolean),
    })),
  };
  if ($('sCode').value.trim()) body.checkCode = $('sCode').value.trim();
  if ($('sPass').value) body.password = $('sPass').value;
  const r = await api('api/settings', body, 'PUT');
  if (r && r.ok) { $('settings').close(); thumbFor = undefined; await loadConfig(); tick(); loadFiles(); }
};

// ---------- start ----------
(async () => {
  await loadConfig();
  if ($('setupBanner').hidden === false) openSettings(); // first run: go straight to settings
  startCamera(); tick(); chart(); loadFiles(); loadHistory(); loadDetail();
  setInterval(tick, 2000);
  setInterval(chart, 10000);
  setInterval(loadHistory, 30000);
  setInterval(loadDetail, 5000);
  setInterval(loadConfig, 30000);
})();
