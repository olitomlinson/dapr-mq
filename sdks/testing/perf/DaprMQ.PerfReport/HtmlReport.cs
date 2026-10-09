using System.Text.Json.Nodes;

namespace DaprMQ.PerfReport;

/// <summary>
/// Writes a self-contained report.html comparing every SDK's runs under one results root: graphs
/// first (one series per SDK), tables underneath. Embeds the history plus the timeline of the latest
/// run for each (SDK, environment, API replicas, scenario) series.
/// </summary>
public static class HtmlReport
{
    public static string Write(string root, string? outPath = null)
    {
        var history = PerfResults.LoadHistory(root);

        var timelines = new JsonObject();
        foreach (var group in history.GroupBy(r => $"{RegressionCheck.SeriesKey(r)}|{RegressionCheck.Str(r, "scenario", "key")}"))
        {
            var latest = group.Last();
            var runId = RegressionCheck.Str(latest, "runId")!;
            var run = PerfResults.LoadRun(root, RegressionCheck.Str(latest, "sdk", "name")!, runId);
            if (run?["timeline"]?["series"] is JsonObject series)
            {
                timelines[runId] = series.DeepClone();
            }
        }

        var data = new JsonObject
        {
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["history"] = new JsonArray(history.Select(h => (JsonNode)h.DeepClone()).ToArray()),
            ["timelines"] = timelines,
        };
        var html = Template.Replace("/*DATA*/null", data.ToJsonString(PerfResults.Json).Replace("</", "<\\/"));

        var path = outPath ?? Path.Combine(root, "report.html");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, html);
        return path;
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DaprMQ SDK Performance</title>
<script src="https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.min.js"></script>
<style>
:root {
  color-scheme: light;
  --surface-0: #f5f4f1; --surface-1: #fcfcfb; --border: #e3e2dd;
  --text-primary: #0b0b0b; --text-secondary: #52514e; --text-muted: #7a7974; --grid: #ecebe7;
  --series-1: #2a78d6; --series-2: #eb6834; --series-3: #1baf7a; --series-4: #eda100;
  --reference: #7a7974; --critical: #d03b3b;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
    --surface-0: #111110; --surface-1: #1a1a19; --border: #2e2e2c;
    --text-primary: #ffffff; --text-secondary: #c3c2b7; --text-muted: #8d8c85; --grid: #2a2a28;
    --series-1: #3987e5; --series-2: #d95926; --series-3: #199e70; --series-4: #c98500;
    --reference: #8d8c85; --critical: #d03b3b;
  }
}
:root[data-theme="dark"] {
  color-scheme: dark;
  --surface-0: #111110; --surface-1: #1a1a19; --border: #2e2e2c;
  --text-primary: #ffffff; --text-secondary: #c3c2b7; --text-muted: #8d8c85; --grid: #2a2a28;
  --series-1: #3987e5; --series-2: #d95926; --series-3: #199e70; --series-4: #c98500;
  --reference: #8d8c85; --critical: #d03b3b;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--surface-0); color: var(--text-primary); font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
main { max-width: 1200px; margin: 0 auto; padding: 24px 16px 48px; }
h1 { font-size: 20px; margin: 0 0 4px; }
h2 { font-size: 15px; margin: 0 0 2px; }
h3 { font-size: 16px; margin: 28px 0 8px; }
p.sub, .card p { color: var(--text-secondary); margin: 0 0 12px; }
code { font-size: 12px; }
.filters { display: flex; flex-wrap: wrap; gap: 12px; margin: 16px 0; position: sticky; top: 0; background: var(--surface-0); padding: 8px 0; z-index: 1; }
.filters label { display: flex; flex-direction: column; font-size: 12px; color: var(--text-secondary); gap: 4px; min-width: 0; }
select { font: inherit; padding: 6px 8px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface-1); color: var(--text-primary); max-width: 100%; }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 520px), 1fr)); gap: 16px; }
.card { background: var(--surface-1); border: 1px solid var(--border); border-radius: 10px; padding: 14px 16px; min-width: 0; }
.chart { position: relative; height: 280px; }
.wide { grid-column: 1 / -1; }
table { width: 100%; border-collapse: collapse; font-size: 12px; font-variant-numeric: tabular-nums; }
th, td { text-align: right; padding: 6px 8px; border-bottom: 1px solid var(--border); white-space: nowrap; }
th:first-child, td:first-child, th:nth-child(2), td:nth-child(2) { text-align: left; }
th { color: var(--text-secondary); font-weight: 500; }
.table-wrap { overflow-x: auto; }
.swatch { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 6px; vertical-align: -1px; }
.fail { color: var(--critical); font-weight: 600; }
.empty { color: var(--text-secondary); padding: 32px 0; }
details summary { cursor: pointer; color: var(--text-secondary); margin: 4px 0 8px; }
</style>
</head>
<body>
<main>
  <h1>DaprMQ SDK performance</h1>
  <p class="sub">Every SDK runs the same profiles (<code>sdks/testing/PERFORMANCE_TESTS.md</code>) against the same Testcontainers stack. Each SDK keeps its colour on every chart. Only runs from the same environment and API replica count are comparable.</p>

  <div class="filters">
    <label>Environment <select id="env"></select></label>
    <label>API replicas <select id="replicas"></select></label>
    <label>Profile <select id="profile"></select></label>
  </div>

  <div id="content">
    <h3>Overview: latest run per SDK</h3>
    <div class="grid">
      <div class="card"><h2>Load throughput, relative to the fastest SDK</h2><p>Messages/s for each load profile as a share of the best SDK on that profile (100% = fastest). Hover for absolute numbers.</p><div class="chart"><canvas id="ov-load"></canvas></div></div>
      <div class="card"><h2>Session drain efficiency</h2><p>Ideal ÷ wall clock for each session-drain profile. Higher is better.</p><div class="chart"><canvas id="ov-drain"></canvas></div></div>
      <div class="card"><h2>Queue drain throughput, relative to the fastest SDK</h2><p>Messages/s for each queue-drain profile (<code>QueueConsumer</code>) as a share of the best SDK (100% = fastest). Hover for absolute numbers.</p><div class="chart"><canvas id="ov-queue"></canvas></div></div>
      <div class="card wide"><details><summary>Overview table</summary><div class="table-wrap"><table id="ov-table"></table></div></details></div>
    </div>

    <h3 id="detail-title"></h3>
    <p class="sub" id="detail-sub"></p>
    <div class="grid" id="detail"></div>

    <h3>All runs of this profile</h3>
    <div class="card"><div class="table-wrap"><table id="runs"></table></div></div>
  </div>
</main>
<script>
const DATA = /*DATA*/null;
const SDKS = ['dotnet', 'python', 'typescript', 'java'];
const SDK_LABEL = { dotnet: '.NET', python: 'Python', typescript: 'TypeScript', java: 'Java' };
const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
const color = sdk => css(`--series-${SDKS.indexOf(sdk) + 1}`) || css('--reference');
const replicas = r => r.topology?.apiReplicas ?? 1;
const isDrain = r => r.scenario.name === 'session-drain';
const isQueueDrain = r => r.scenario.name === 'queue-drain';
const isRamp = r => (r.steps?.length ?? 0) > 1;
const fmt = (v, d = 1) => v == null || Number.isNaN(v) ? '–' : Number(v).toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d });
const when = r => new Date(r.timestampUtc).toISOString().slice(0, 16).replace('T', ' ');
const charts = {};

// Name each line at its last point too, so identity never rests on colour alone.
const endLabels = {
  id: 'endLabels',
  afterDatasetsDraw(chart, _, opts) {
    if (!opts || !opts.enabled) return;
    const { ctx } = chart;
    ctx.save();
    ctx.font = '12px system-ui, sans-serif';
    ctx.fillStyle = css('--text-secondary');
    ctx.textBaseline = 'middle';
    chart.data.datasets.forEach((ds, i) => {
      const meta = chart.getDatasetMeta(i);
      if (ds.noEndLabel || meta.hidden) return;
      const pts = meta.data.filter((p, j) => ds.data[j] != null && (ds.data[j].y ?? ds.data[j]) != null);
      const last = pts[pts.length - 1];
      if (last) ctx.fillText(ds.label, last.x + 6, last.y);
    });
    ctx.restore();
  },
};
Chart.register(endLabels);

function option(select, value, text) {
  const o = document.createElement('option');
  o.value = value; o.textContent = text; select.appendChild(o);
}

function fill(select, values, label, keep) {
  const prev = keep ? select.value : null;
  select.replaceChildren();
  values.forEach(v => option(select, v, label(v)));
  if (prev && values.includes(prev)) select.value = prev;
}

function scoped() {
  const env = document.getElementById('env').value;
  const reps = +document.getElementById('replicas').value;
  return DATA.history.filter(r => r.environment.label === env && replicas(r) === reps);
}

// Latest run per (scenario key, sdk) within the current environment + replica scope.
function latestBy(runs) {
  const m = new Map();
  runs.forEach(r => m.set(`${r.scenario.key}|${r.sdk.name}`, r));
  return m;
}

function profileKeys(runs) {
  // Load profiles first, then session drain, then queue drain; pr before extreme; keep first-seen order otherwise.
  const seen = new Map();
  runs.forEach(r => seen.set(r.scenario.key, r));
  const rank = r => (isDrain(r) ? 2 : 0) + (isQueueDrain(r) ? 4 : 0) + (isRamp(r) ? 1 : 0);
  return [...seen.values()].sort((a, b) => rank(a) - rank(b)).map(r => r.scenario.key);
}

function profileName(key) {
  const r = [...DATA.history].reverse().find(x => x.scenario.key === key);
  return r ? r.scenario.profile : key;
}

function setup() {
  if (!DATA.history.length) {
    document.getElementById('content').innerHTML = '<p class="empty">No runs recorded yet.</p>';
    return;
  }
  const latest = DATA.history[DATA.history.length - 1];
  const envSel = document.getElementById('env');
  fill(envSel, [...new Set(DATA.history.map(r => r.environment.label))], e => e);
  envSel.value = latest.environment.label;
  envSel.onchange = () => { fillReplicas(); render(); };
  document.getElementById('replicas').onchange = () => { fillProfiles(); render(); };
  document.getElementById('profile').onchange = render;
  fillReplicas(replicas(latest));
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', render);
  render();
}

function fillReplicas(initial) {
  const env = document.getElementById('env').value;
  const sel = document.getElementById('replicas');
  const values = [...new Set(DATA.history.filter(r => r.environment.label === env).map(r => String(replicas(r))))].sort((a, b) => a - b);
  fill(sel, values, v => v === '1' ? '1 (no load balancer)' : `${v} behind nginx`, true);
  if (initial != null && values.includes(String(initial))) sel.value = String(initial);
  fillProfiles();
}

function fillProfiles() {
  fill(document.getElementById('profile'), profileKeys(scoped()), k => `${profileName(k)} — ${k}`, true);
}

function baseOptions(yTitle, { xTitle, xType = 'category', endLabelsOn = true, xTicks } = {}) {
  const text = css('--text-secondary'), grid = css('--grid');
  return {
    responsive: true, maintainAspectRatio: false, animation: false,
    interaction: { mode: xType === 'category' ? 'index' : 'nearest', axis: 'x', intersect: false },
    layout: { padding: { right: endLabelsOn ? 76 : 8 } },
    plugins: {
      legend: { position: 'bottom', labels: { color: text, boxWidth: 14, boxHeight: 2 } },
      tooltip: { backgroundColor: css('--surface-1'), titleColor: css('--text-primary'), bodyColor: css('--text-primary'), borderColor: css('--border'), borderWidth: 1 },
      endLabels: { enabled: endLabelsOn },
    },
    scales: {
      x: { type: xType, title: { display: !!xTitle, text: xTitle, color: text }, ticks: { color: text, maxRotation: 0, autoSkip: true, ...(xTicks || {}) }, grid: { display: false } },
      y: { beginAtZero: true, title: { display: true, text: yTitle, color: text }, ticks: { color: text }, grid: { color: grid } },
    },
  };
}

function draw(id, config) {
  charts[id]?.destroy();
  charts[id] = new Chart(document.getElementById(id), config);
}

function line(sdk, data, extra = {}) {
  const c = color(sdk);
  return { label: SDK_LABEL[sdk] || sdk, data, borderColor: c, backgroundColor: c, borderWidth: 2, pointRadius: 0, pointHoverRadius: 5, tension: 0, spanGaps: false, ...extra };
}

function bar(label, data, c, extra = {}) {
  return { label, data, backgroundColor: c, borderColor: css('--surface-1'), borderWidth: 2, borderRadius: 4, borderSkipped: 'bottom', maxBarThickness: 36, ...extra };
}

function card(container, title, desc, { wide = false, table = false } = {}) {
  const id = `c${Object.keys(charts).length}-${Math.random().toString(36).slice(2, 7)}`;
  const div = document.createElement('div');
  div.className = 'card' + (wide ? ' wide' : '');
  const h = document.createElement('h2'); h.textContent = title; div.appendChild(h);
  const p = document.createElement('p'); p.textContent = desc; div.appendChild(p);
  if (table) {
    const w = document.createElement('div'); w.className = 'table-wrap';
    const t = document.createElement('table'); t.id = id; w.appendChild(t); div.appendChild(w);
  } else {
    const w = document.createElement('div'); w.className = 'chart';
    const c = document.createElement('canvas'); c.id = id; w.appendChild(c); div.appendChild(w);
  }
  container.appendChild(div);
  return id;
}

function sdkCell(row, sdk) {
  const td = row.insertCell();
  const s = document.createElement('span'); s.className = 'swatch'; s.style.background = color(sdk);
  td.append(s, SDK_LABEL[sdk] || sdk);
}

function table(id, cols, rows) {
  const t = document.getElementById(id);
  t.replaceChildren();
  const head = t.createTHead().insertRow();
  cols.forEach(([h]) => { const th = document.createElement('th'); th.textContent = h; head.appendChild(th); });
  const body = t.createTBody();
  rows.forEach(r => {
    const row = body.insertRow();
    cols.forEach(([h, f]) => {
      if (h === 'SDK') { sdkCell(row, r.sdk.name); return; }
      const v = f(r);
      const td = row.insertCell();
      if (v && typeof v === 'object') { td.textContent = v.text; td.className = v.cls || ''; } else td.textContent = v;
    });
  });
}

const status = r => r.checks?.passed === false ? { text: '✗ ' + (r.checks.failures || []).join('; '), cls: 'fail' } : '✓';

function render() {
  Object.values(charts).forEach(c => c.destroy());
  for (const k in charts) delete charts[k];
  const runs = scoped();
  const latest = latestBy(runs);
  const keys = profileKeys(runs);
  const present = SDKS.filter(s => runs.some(r => r.sdk.name === s));
  renderOverview(runs, latest, keys, present);
  renderDetail(runs, document.getElementById('profile').value, present);
}

function renderOverview(runs, latest, keys, present) {
  const get = (k, s) => latest.get(`${k}|${s}`);
  const loadKeys = keys.filter(k => runs.some(r => r.scenario.key === k && !isDrain(r) && !isQueueDrain(r) && !isRamp(r)));
  const drainKeys = keys.filter(k => runs.some(r => r.scenario.key === k && isDrain(r)));
  const queueKeys = keys.filter(k => runs.some(r => r.scenario.key === k && isQueueDrain(r)));

  const loadOpts = baseOptions('% of fastest SDK', { endLabelsOn: false });
  loadOpts.plugins.legend.labels = { ...loadOpts.plugins.legend.labels, boxWidth: 10, boxHeight: 10 };
  loadOpts.plugins.tooltip.callbacks = {
    label: ctx => {
      const r = get(loadKeys[ctx.dataIndex], present[ctx.datasetIndex]);
      return r ? `${ctx.dataset.label}: ${fmt(ctx.raw, 0)}% (${fmt(r.metrics.messagesPerSecond, 0)} msg/s, p95 ${fmt(r.metrics.latencyMs.p95)} ms)` : `${ctx.dataset.label}: no run`;
    },
  };
  draw('ov-load', { type: 'bar', data: { labels: loadKeys.map(profileName), datasets: present.map(s => bar(SDK_LABEL[s], loadKeys.map(k => {
    const best = Math.max(...present.map(x => get(k, x)?.metrics.messagesPerSecond ?? 0));
    const v = get(k, s)?.metrics.messagesPerSecond;
    return v == null || best === 0 ? null : +(v / best * 100).toFixed(1);
  }), color(s))) }, options: loadOpts });

  const drainOpts = baseOptions('efficiency %', { endLabelsOn: false });
  drainOpts.plugins.legend.labels = { ...drainOpts.plugins.legend.labels, boxWidth: 10, boxHeight: 10 };
  drainOpts.plugins.tooltip.callbacks = {
    label: ctx => {
      const r = get(drainKeys[ctx.dataIndex], present[ctx.datasetIndex]);
      return r ? `${ctx.dataset.label}: ${fmt(ctx.raw)}% (wall ${fmt(r.metrics.wallClockSeconds)} s, ideal ${fmt(r.metrics.idealSeconds)} s)` : `${ctx.dataset.label}: no run`;
    },
  };
  draw('ov-drain', { type: 'bar', data: { labels: drainKeys.map(profileName), datasets: present.map(s => bar(SDK_LABEL[s], drainKeys.map(k => {
    const v = get(k, s)?.metrics.efficiency;
    return v == null ? null : +(v * 100).toFixed(1);
  }), color(s))) }, options: drainOpts });

  const queueOpts = baseOptions('% of fastest SDK', { endLabelsOn: false });
  queueOpts.plugins.legend.labels = { ...queueOpts.plugins.legend.labels, boxWidth: 10, boxHeight: 10 };
  queueOpts.plugins.tooltip.callbacks = {
    label: ctx => {
      const r = get(queueKeys[ctx.dataIndex], present[ctx.datasetIndex]);
      return r ? `${ctx.dataset.label}: ${fmt(ctx.raw, 0)}% (${fmt(r.metrics.messagesPerSecond, 0)} msg/s, delivery p95 ${fmt(r.metrics.deliveryLatencyMs.p95, 0)} ms)` : `${ctx.dataset.label}: no run`;
    },
  };
  draw('ov-queue', { type: 'bar', data: { labels: queueKeys.map(profileName), datasets: present.map(s => bar(SDK_LABEL[s], queueKeys.map(k => {
    const best = Math.max(...present.map(x => get(k, x)?.metrics.messagesPerSecond ?? 0));
    const v = get(k, s)?.metrics.messagesPerSecond;
    return v == null || best === 0 ? null : +(v / best * 100).toFixed(1);
  }), color(s))) }, options: queueOpts });

  const t = document.getElementById('ov-table');
  t.replaceChildren();
  const head = t.createTHead().insertRow();
  ['Profile', 'Headline'].concat(present.map(s => SDK_LABEL[s])).forEach(h => { const th = document.createElement('th'); th.textContent = h; head.appendChild(th); });
  const body = t.createTBody();
  keys.forEach(k => {
    const sample = runs.find(r => r.scenario.key === k);
    const row = body.insertRow();
    row.insertCell().textContent = profileName(k);
    row.insertCell().textContent = isDrain(sample) ? 'wall clock s / efficiency' : isQueueDrain(sample) ? 'msg/s / delivery p95 ms' : 'msg/s / p95 ms';
    present.forEach(s => {
      const r = get(k, s);
      const td = row.insertCell();
      if (!r) { td.textContent = '–'; return; }
      td.textContent = isDrain(r) ? `${fmt(r.metrics.wallClockSeconds)} / ${fmt(r.metrics.efficiency * 100)}%`
        : isQueueDrain(r) ? `${fmt(r.metrics.messagesPerSecond, 0)} / ${fmt(r.metrics.deliveryLatencyMs.p95, 0)}`
        : `${fmt(r.metrics.messagesPerSecond, 0)} / ${fmt(r.metrics.latencyMs.p95)}`;
      if (r.checks?.passed === false) td.className = 'fail';
    });
  });
}

function timelinePoints(r, series) {
  const values = DATA.timelines[r.runId]?.[series];
  return values ? values.map((y, x) => ({ x, y })) : [];
}

function trendPoints(runsOfSdk, f) {
  return runsOfSdk.map(r => ({ x: Date.parse(r.timestampUtc), y: f(r), run: r }));
}


function trendChart(container, title, desc, yTitle, runs, present, f) {
  const id = card(container, title, desc);
  // Pad the time axis to at least an hour either side, and label by time of day when runs span under two days.
  const xs = runs.map(r => Date.parse(r.timestampUtc));
  const lo = Math.min(...xs), hi = Math.max(...xs), pad = Math.max((hi - lo) * 0.05, 3600e3);
  const sameDays = hi - lo < 2 * 86400e3;
  const opts = baseOptions(yTitle, { xType: 'linear', xTicks: { maxTicksLimit: 8, callback: v => { const d = new Date(v).toISOString(); return sameDays ? d.slice(5, 16).replace('T', ' ') : d.slice(5, 10); } } });
  opts.scales.x.min = lo - pad; opts.scales.x.max = hi + pad;
  opts.plugins.tooltip.callbacks = {
    title: items => items.length ? when(items[0].raw.run) : '',
    label: ctx => `${ctx.dataset.label}: ${fmt(ctx.raw.y)} (${ctx.raw.run.environment.gitBranch || '?'} ${(ctx.raw.run.environment.gitSha || '').slice(0, 7)})`,
  };
  draw(id, { type: 'line', data: { datasets: present.map(s => line(s, trendPoints(runs.filter(r => r.sdk.name === s), f), { pointRadius: 4 })) }, options: opts });
}

function timelineChart(container, title, desc, yTitle, latestRuns, series, extra = {}) {
  const id = card(container, title, desc, extra);
  const opts = baseOptions(yTitle, { xType: 'linear', xTitle: 'seconds into the recorded window' });
  opts.plugins.tooltip.callbacks = { title: items => items.length ? `t = ${items[0].raw.x} s` : '' };
  draw(id, { type: 'line', data: { datasets: latestRuns.map(r => line(r.sdk.name, timelinePoints(r, series))) }, options: opts });
}

function renderDetail(allRuns, key, present) {
  const container = document.getElementById('detail');
  container.replaceChildren();
  const runs = allRuns.filter(r => r.scenario.key === key);
  const latestRuns = SDKS.map(s => runs.filter(r => r.sdk.name === s).pop()).filter(Boolean);
  const sample = runs[runs.length - 1];
  document.getElementById('detail-title').textContent = sample ? `Profile ${sample.scenario.profile} (${sample.scenario.id} ${sample.scenario.name})` : '';
  document.getElementById('detail-sub').textContent = sample ? `Key ${key}. Timelines and percentile bars show each SDK's latest run; trends show every run.` : '';
  if (!sample) { renderRuns([]); return; }
  const sdksHere = SDKS.filter(s => runs.some(r => r.sdk.name === s));

  if (isDrain(sample)) {
    timelineChart(container, 'Busy slots over time', 'Average handlers running per second, latest run per SDK.', 'busy slots', latestRuns, 'busySlots');
    const idleId = card(container, 'Where peak idle time went', 'Idle slot-seconds in the peak window, latest run per SDK, by cause.');
    const idleOpts = baseOptions('slot-seconds', { endLabelsOn: false });
    idleOpts.scales.x.stacked = true; idleOpts.scales.y.stacked = true;
    idleOpts.plugins.legend.labels = { ...idleOpts.plugins.legend.labels, boxWidth: 10, boxHeight: 10 };
    const causes = [['Drain wait', 'drainWaitSeconds'], ['Claim', 'claimSeconds'], ['In-session wait', 'inSessionWaitSeconds'], ['Between streams', 'betweenStreamsSeconds']];
    draw(idleId, { type: 'bar', data: { labels: latestRuns.map(r => SDK_LABEL[r.sdk.name]), datasets: causes.map(([label, f], i) => bar(label, latestRuns.map(r => r.metrics.peak[f]), css(`--series-${i + 1}`))) }, options: idleOpts });
    trendChart(container, 'Wall clock', 'Consume start to last message handled, every run.', 'seconds', runs, sdksHere, r => r.metrics.wallClockSeconds);
    trendChart(container, 'Efficiency', 'Ideal ÷ wall clock, every run.', '%', runs, sdksHere, r => r.metrics.efficiency * 100);
    trendChart(container, 'Claim latency p95', 'Stream opened to first delivery.', 'ms', runs, sdksHere, r => r.metrics.claimLatencyMs.p95);
    trendChart(container, 'Delivery latency p95', 'Enqueue to handler start.', 'ms', runs, sdksHere, r => r.metrics.deliveryLatencyMs?.p95);
  } else if (isQueueDrain(sample)) {
    timelineChart(container, 'Throughput over the drain', 'Messages handled per second, latest run per SDK.', 'messages/s', latestRuns, 'messagesPerSecond');
    timelineChart(container, 'Busy handlers over time', 'Average handlers running per second, latest run per SDK.', 'handlers', latestRuns, 'busyHandlers');
    trendChart(container, 'Throughput trend', 'Messages/s of every run.', 'messages/s', runs, sdksHere, r => r.metrics.messagesPerSecond);
    trendChart(container, 'Wall clock', 'Consume start to last message handled, every run.', 'seconds', runs, sdksHere, r => r.metrics.wallClockSeconds);
    trendChart(container, 'Delivery latency p95', 'Publish to handler start.', 'ms', runs, sdksHere, r => r.metrics.deliveryLatencyMs.p95);
    if (runs.some(r => r.metrics.efficiency != null)) {
      trendChart(container, 'Efficiency', 'Ideal ÷ wall clock, every run.', '%', runs, sdksHere, r => r.metrics.efficiency == null ? null : r.metrics.efficiency * 100);
    }
  } else if (isRamp(sample)) {
    const conc = [...new Set(runs.flatMap(r => r.steps.map(s => s.concurrency)))].sort((a, b) => a - b);
    const stepChart = (title, desc, yTitle, f) => {
      const id = card(container, title, desc);
      draw(id, { type: 'line', data: { labels: conc, datasets: latestRuns.map(r => line(r.sdk.name, conc.map(c => { const s = r.steps.find(x => x.concurrency === c); return s ? f(s) : null; }), { pointRadius: 4 })) }, options: baseOptions(yTitle, { xTitle: 'concurrent workers' }) });
    };
    stepChart('Throughput vs concurrency', 'Messages/s at each step of the latest ramp. Where the line flattens is where adding workers stops helping.', 'messages/s', s => s.messagesPerSecond);
    stepChart('p95 latency vs concurrency', 'Per-operation p95 at each step. A sharp rise marks saturation.', 'ms', s => s.latencyMs.p95);
    stepChart('p99 latency vs concurrency', 'Per-operation p99 at each step.', 'ms', s => s.latencyMs.p99);
    stepChart('Errors vs concurrency', 'Failed operations at each step.', 'errors', s => s.errors);
    timelineChart(container, 'Throughput over the ramp', 'Messages/s per second across every step of the latest run.', 'messages/s', latestRuns, 'messagesPerSecond', { wide: true });
    trendChart(container, 'Peak throughput', 'Best step of each run.', 'messages/s', runs, sdksHere, r => Math.max(...r.steps.map(s => s.messagesPerSecond)));
  } else {
    timelineChart(container, 'Throughput over the run', 'Messages/s per second, latest run per SDK.', 'messages/s', latestRuns, 'messagesPerSecond');
    timelineChart(container, 'p95 latency over the run', 'Per-operation p95 of the operations that finished in each second.', 'ms', latestRuns, 'latencyP95Ms');
    const pId = card(container, 'Latency percentiles', 'Per-operation latency of the latest run per SDK.');
    const pOpts = baseOptions('ms', { endLabelsOn: false });
    pOpts.plugins.legend.labels = { ...pOpts.plugins.legend.labels, boxWidth: 10, boxHeight: 10 };
    draw(pId, { type: 'bar', data: { labels: ['p50', 'p95', 'p99'], datasets: latestRuns.map(r => bar(SDK_LABEL[r.sdk.name], [r.metrics.latencyMs.p50, r.metrics.latencyMs.p95, r.metrics.latencyMs.p99], color(r.sdk.name))) }, options: pOpts });
    trendChart(container, 'Throughput trend', 'Messages/s of every run.', 'messages/s', runs, sdksHere, r => r.metrics.messagesPerSecond);
    trendChart(container, 'p95 latency trend', 'Per-operation p95 of every run.', 'ms', runs, sdksHere, r => r.metrics.latencyMs.p95);
  }

  const tId = card(container, 'Latest run per SDK', 'The numbers behind the charts above.', { wide: true, table: true });
  table(tId, detailColumns(sample), latestRuns);
  renderRuns(runs);
}

function detailColumns(sample) {
  const common = [['SDK', null], ['Run', r => when(r)], ['Version', r => [r.sdk.version, r.sdk.runtime].filter(Boolean).join(' · ') || '–']];
  if (isDrain(sample)) return common.concat([
    ['Wall (s)', r => fmt(r.metrics.wallClockSeconds)], ['Ideal (s)', r => fmt(r.metrics.idealSeconds)],
    ['Efficiency', r => fmt(r.metrics.efficiency * 100) + '%'], ['Peak util', r => fmt(r.metrics.peak.utilization * 100) + '%'],
    ['Claim p95 (ms)', r => fmt(r.metrics.claimLatencyMs.p95, 0)], ['Msg gap p95 (ms)', r => fmt(r.metrics.interMessageGapMs.p95, 0)],
    ['Delivery p95 (ms)', r => fmt(r.metrics.deliveryLatencyMs?.p95, 0)], ['Checks', status],
  ]);
  if (isQueueDrain(sample)) return common.concat([
    ['Msg/s', r => fmt(r.metrics.messagesPerSecond, 0)], ['Wall (s)', r => fmt(r.metrics.wallClockSeconds)],
    ['Ideal (s)', r => fmt(r.metrics.idealSeconds)], ['Efficiency', r => r.metrics.efficiency == null ? '–' : fmt(r.metrics.efficiency * 100) + '%'],
    ['Peak handlers', r => r.metrics.peakConcurrentHandlers], ['Delivery p95 (ms)', r => fmt(r.metrics.deliveryLatencyMs.p95, 0)],
    ['First msg (s)', r => fmt(r.metrics.timeToFirstMessageSeconds, 2)], ['Checks', status],
  ]);
  if (isRamp(sample)) return common.concat([
    ['Peak msg/s', r => fmt(Math.max(...r.steps.map(s => s.messagesPerSecond)), 0)],
    ['At workers', r => { const b = r.steps.reduce((a, s) => s.messagesPerSecond > a.messagesPerSecond ? s : a); return b.concurrency; }],
    ['Steps', r => r.steps.map(s => `${s.concurrency}:${fmt(s.messagesPerSecond, 0)}`).join(' ')],
    ['Errors', r => r.metrics.errors], ['Checks', status],
  ]);
  return common.concat([
    ['Msg/s', r => fmt(r.metrics.messagesPerSecond, 0)], ['Ops/s', r => fmt(r.metrics.opsPerSecond, 0)],
    ['p50 (ms)', r => fmt(r.metrics.latencyMs.p50, 2)], ['p95 (ms)', r => fmt(r.metrics.latencyMs.p95, 2)],
    ['p99 (ms)', r => fmt(r.metrics.latencyMs.p99, 2)], ['Max (ms)', r => fmt(r.metrics.latencyMs.max, 1)],
    ['Ops', r => r.metrics.ops], ['Errors', r => r.metrics.errors], ['Checks', status],
  ]);
}

function renderRuns(runs) {
  if (!runs.length) { document.getElementById('runs').replaceChildren(); return; }
  const cols = [['SDK', null], ['Run', r => when(r)], ['Branch', r => (r.environment.gitBranch || '?') + (r.environment.gitDirty ? '*' : '')], ['Commit', r => (r.environment.gitSha || '').slice(0, 7)], ['Scale', r => r.scale]]
    .concat(detailColumns(runs[0]).slice(3));
  table('runs', cols, [...runs].reverse());
}

setup();
</script>
</body>
</html>
""";
}
