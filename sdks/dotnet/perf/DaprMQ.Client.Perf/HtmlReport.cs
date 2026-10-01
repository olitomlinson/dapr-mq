using System.Text.Json.Nodes;

namespace DaprMQ.Client.Perf;

/// <summary>
/// Writes a self-contained results/report.html: trends across every run in history.jsonl, plus
/// the busy-slots timeline of the latest run for each (environment, scenario) series.
/// </summary>
public static class HtmlReport
{
    public static string Write(string outDir)
    {
        var history = ResultStore.LoadHistory(outDir);

        var latestTimelines = new JsonObject();
        foreach (var group in history.GroupBy(SeriesKey))
        {
            var latest = group.OrderBy(r => (string)r["timestampUtc"]!).Last();
            var run = ResultStore.LoadRun(outDir, (string)latest["runId"]!);
            if (run?["metrics"]?["busySlotsTimeline"] is JsonArray timeline)
            {
                latestTimelines[group.Key] = timeline.DeepClone();
            }
        }

        var data = new JsonObject { ["history"] = new JsonArray(history.Select(h => (JsonNode)h.DeepClone()).ToArray()), ["timelines"] = latestTimelines };
        var html = Template.Replace("/*DATA*/null", data.ToJsonString(ResultStore.Json).Replace("</", "<\\/"));

        var path = Path.Combine(outDir, "report.html");
        File.WriteAllText(path, html);
        return path;
    }

    private static string SeriesKey(JsonObject run)
    {
        return $"{run["environment"]!["label"]}|{run["scenario"]!["key"]}";
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Session Drain Performance</title>
<script src="https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.min.js"></script>
<style>
:root {
  color-scheme: light;
  --surface-0: #f5f4f1; --surface-1: #fcfcfb; --border: #e3e2dd;
  --text-primary: #0b0b0b; --text-secondary: #52514e; --text-muted: #7a7974; --grid: #ecebe7;
  --series-1: #2a78d6; --series-2: #eb6834; --series-3: #1baf7a; --series-4: #eda100;
  --reference: #7a7974;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
    --surface-0: #111110; --surface-1: #1a1a19; --border: #2e2e2c;
    --text-primary: #ffffff; --text-secondary: #c3c2b7; --text-muted: #8d8c85; --grid: #2a2a28;
    --series-1: #3987e5; --series-2: #d95926; --series-3: #199e70; --series-4: #c98500;
    --reference: #8d8c85;
  }
}
:root[data-theme="dark"] {
  color-scheme: dark;
  --surface-0: #111110; --surface-1: #1a1a19; --border: #2e2e2c;
  --text-primary: #ffffff; --text-secondary: #c3c2b7; --text-muted: #8d8c85; --grid: #2a2a28;
  --series-1: #3987e5; --series-2: #d95926; --series-3: #199e70; --series-4: #c98500;
  --reference: #8d8c85;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--surface-0); color: var(--text-primary); font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
main { max-width: 1120px; margin: 0 auto; padding: 24px 16px 48px; }
h1 { font-size: 20px; margin: 0 0 4px; }
h2 { font-size: 15px; margin: 0 0 2px; }
p.sub, .card p { color: var(--text-secondary); margin: 0 0 12px; }
.filters { display: flex; flex-wrap: wrap; gap: 12px; margin: 16px 0; }
.filters label { display: flex; flex-direction: column; font-size: 12px; color: var(--text-secondary); gap: 4px; }
select { font: inherit; padding: 6px 8px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface-1); color: var(--text-primary); max-width: 100%; }
.tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 16px; }
.tile, .card { background: var(--surface-1); border: 1px solid var(--border); border-radius: 10px; padding: 14px 16px; }
.tile .label { font-size: 12px; color: var(--text-secondary); }
.tile .value { font-size: 26px; font-weight: 600; font-variant-numeric: tabular-nums; }
.tile .note { font-size: 12px; color: var(--text-muted); }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 480px), 1fr)); gap: 16px; }
.card { min-width: 0; }
.chart { position: relative; height: 260px; }
table { width: 100%; border-collapse: collapse; font-size: 12px; font-variant-numeric: tabular-nums; }
th, td { text-align: right; padding: 6px 8px; border-bottom: 1px solid var(--border); white-space: nowrap; }
th:first-child, td:first-child, th:nth-child(2), td:nth-child(2) { text-align: left; }
th { color: var(--text-secondary); font-weight: 500; }
.table-wrap { overflow-x: auto; }
.empty { color: var(--text-secondary); padding: 32px 0; }
</style>
</head>
<body>
<main>
  <h1>Session drain performance</h1>
  <p class="sub">One <code>SessionQueueConsumer</code> draining N sessions × M messages, each message taking a fixed settle time. Idle time is measured over the peak window: first delivery until the last session is first claimed.</p>

  <div class="filters">
    <label>Scenario <select id="scenario"></select></label>
    <label>Environment <select id="env"></select></label>
  </div>

  <div id="content">
    <div class="tiles" id="tiles"></div>
    <div class="grid">
      <div class="card"><h2>Wall clock vs ideal</h2><p>Seconds from consumer start (in +concurrent scenarios, also publish start) to the last message handled. Ideal = ⌈sessions ÷ slots⌉ × messages × settle, or a session's publish time + settle if a rate-limited concurrent publish is slower.</p><div class="chart"><canvas id="wall"></canvas></div></div>
      <div class="card"><h2>Peak slot utilisation</h2><p>Share of slot time spent in the handler during the peak window.</p><div class="chart"><canvas id="util"></canvas></div></div>
      <div class="card"><h2>Where peak idle time went</h2><p>Idle slot-seconds in the peak window, by cause.</p><div class="chart"><canvas id="idle"></canvas></div></div>
      <div class="card"><h2>Busy slots over time (latest run)</h2><p>Average handlers running per second; dashed line is MaxConcurrentSessions.</p><div class="chart"><canvas id="timeline"></canvas></div></div>
    </div>
    <div class="card" style="margin-top:16px">
      <h2>Runs</h2>
      <div class="table-wrap"><table id="runs"></table></div>
    </div>
  </div>
</main>
<script>
const DATA = /*DATA*/null;
const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
const scenarioKey = r => r.scenario.key; // ScenarioParams.Key, serialised with every run
const runLabel = r => `${r.timestampUtc.slice(0, 16).replace('T', ' ')} ${(r.environment.gitSha || '').slice(0, 7)}`;
const fmt = (v, d = 1) => Number(v).toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d });
const duration = sec => sec < 120 ? `${fmt(sec)} s` : sec < 7200 ? `${fmt(sec / 60)} min` : `${fmt(sec / 3600, 2)} h`;
const charts = {};

function option(select, value, text) {
  const o = document.createElement('option');
  o.value = value; o.textContent = text; select.appendChild(o);
}

function setup() {
  const history = DATA.history;
  if (!history.length) {
    document.getElementById('content').innerHTML = '<p class="empty">No runs recorded yet.</p>';
    return;
  }
  const scenarios = new Map(history.map(r => [scenarioKey(r), r.profile])); // latest profile name per key
  const latest = history[history.length - 1];
  const scenarioSel = document.getElementById('scenario');
  scenarios.forEach((profile, key) => option(scenarioSel, key, `${profile} — ${key}`));
  scenarioSel.value = scenarioKey(latest);

  const envSel = document.getElementById('env');
  [...new Set(history.map(r => r.environment.label))].forEach(e => option(envSel, e, e));
  envSel.value = latest.environment.label;

  scenarioSel.onchange = envSel.onchange = render;
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', render);
  render();
}

function baseOptions(yTitle, extra = {}) {
  const text = css('--text-secondary'), grid = css('--grid');
  return {
    responsive: true, maintainAspectRatio: false, animation: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      legend: { position: 'bottom', labels: { color: text, boxWidth: 14, boxHeight: 2 } },
      tooltip: { backgroundColor: css('--surface-1'), titleColor: css('--text-primary'), bodyColor: css('--text-primary'), borderColor: css('--border'), borderWidth: 1 }
    },
    scales: {
      x: { ticks: { color: text, maxRotation: 0, autoSkip: true }, grid: { display: false } },
      y: { beginAtZero: true, title: { display: true, text: yTitle, color: text }, ticks: { color: text }, grid: { color: grid } },
    },
    ...extra,
  };
}

function draw(id, config) {
  charts[id]?.destroy();
  charts[id] = new Chart(document.getElementById(id), config);
}

function line(label, data, color, extra = {}) {
  return { label, data, borderColor: color, backgroundColor: color, borderWidth: 2, pointRadius: 4, pointHoverRadius: 6, tension: 0, ...extra };
}

function tile(label, value, note) {
  const t = document.createElement('div'); t.className = 'tile';
  [['label', label], ['value', value], ['note', note]].forEach(([c, v]) => {
    const d = document.createElement('div'); d.className = c; d.textContent = v; t.appendChild(d);
  });
  return t;
}

function render() {
  const scenario = document.getElementById('scenario').value;
  const env = document.getElementById('env').value;
  const runs = DATA.history.filter(r => scenarioKey(r) === scenario && r.environment.label === env);
  const labels = runs.map(runLabel);
  const m = runs.map(r => r.metrics);
  const s1 = css('--series-1'), s2 = css('--series-2'), s3 = css('--series-3'), s4 = css('--series-4'), ref = css('--reference');

  const tiles = document.getElementById('tiles');
  tiles.replaceChildren();
  if (runs.length) {
    const last = m[m.length - 1], prev = m.length > 1 ? m[m.length - 2] : null;
    const delta = (a, b, unit, d = 1) => b == null ? 'first run' : `${a - b >= 0 ? '+' : ''}${fmt(a - b, d)}${unit} vs previous`;
    tiles.append(
      tile('Wall clock', duration(last.wallClockSeconds), delta(last.wallClockSeconds, prev?.wallClockSeconds, 's', 0)),
      tile('Efficiency', `${fmt(last.efficiency * 100)}%`, `ideal ${duration(last.idealSeconds)}`),
      tile('Peak idle', `${fmt((1 - last.peak.utilization) * 100)}%`, `${fmt(last.peak.idleSlotSeconds, 0)} slot-seconds`),
      tile('Drain wait p50', `${fmt(last.drainWaitMs.p50 / 1000)} s`, `claim p50 ${fmt(last.claimLatencyMs.p50, 0)} ms`),
    );
  }

  draw('wall', { type: 'line', data: { labels, datasets: [
    line('Wall clock', m.map(x => x.wallClockSeconds), s1),
    line('Ideal', m.map(x => x.idealSeconds), ref, { borderDash: [6, 4], pointRadius: 0 }),
  ] }, options: baseOptions('seconds') });

  draw('util', { type: 'line', data: { labels, datasets: [
    line('Peak utilisation', m.map(x => +(x.peak.utilization * 100).toFixed(1)), s1),
  ] }, options: baseOptions('% of slot time', { plugins: { ...baseOptions('').plugins, legend: { display: false } } }) });

  const idleOpts = baseOptions('slot-seconds');
  idleOpts.scales.x.stacked = true; idleOpts.scales.y.stacked = true;
  idleOpts.plugins.legend.labels = { ...idleOpts.plugins.legend.labels, boxWidth: 10, boxHeight: 10 };
  const bar = (label, data, color) => ({ label, data, backgroundColor: color, borderColor: css('--surface-1'), borderWidth: { top: 2 }, borderRadius: 4, borderSkipped: 'bottom', maxBarThickness: 40 });
  draw('idle', { type: 'bar', data: { labels, datasets: [
    bar('Drain wait', m.map(x => x.peak.drainWaitSeconds), s1),
    bar('Claim', m.map(x => x.peak.claimSeconds), s2),
    bar('In-session wait', m.map(x => x.peak.inSessionWaitSeconds), s3),
    bar('Between streams', m.map(x => x.peak.betweenStreamsSeconds), s4),
  ] }, options: idleOpts });

  const timeline = DATA.timelines[`${env}|${scenario}`] || [];
  const slots = runs.length ? runs[runs.length - 1].scenario.maxConcurrentSessions : 0;
  const tlOpts = baseOptions('busy slots');
  tlOpts.scales.x.title = { display: true, text: 'seconds since consume start', color: css('--text-secondary') };
  draw('timeline', { type: 'line', data: { labels: timeline.map((_, i) => i), datasets: [
    line('Busy slots', timeline, s1, { pointRadius: 0, fill: true, backgroundColor: s1 + '33' }),
    line('MaxConcurrentSessions', timeline.map(() => slots), ref, { borderDash: [6, 4], pointRadius: 0 }),
  ] }, options: tlOpts });

  const table = document.getElementById('runs');
  table.replaceChildren();
  const cols = [
    ['Run', r => runLabel(r)], ['Branch', r => r.environment.gitBranch + (r.environment.gitDirty ? '*' : '')],
    ['Wall (s)', r => fmt(r.metrics.wallClockSeconds, 0)], ['Ideal (s)', r => fmt(r.metrics.idealSeconds, 0)],
    ['Efficiency', r => fmt(r.metrics.efficiency * 100) + '%'], ['Peak util', r => fmt(r.metrics.peak.utilization * 100) + '%'],
    ['Drain (s)', r => fmt(r.metrics.peak.drainWaitSeconds, 0)], ['Claim (s)', r => fmt(r.metrics.peak.claimSeconds, 0)],
    ['In-session (s)', r => fmt(r.metrics.peak.inSessionWaitSeconds, 0)], ['Between (s)', r => fmt(r.metrics.peak.betweenStreamsSeconds, 0)],
    ['Claim p95 (ms)', r => fmt(r.metrics.claimLatencyMs.p95, 0)],
    ['Delivery p95 (ms)', r => r.metrics.deliveryLatencyMs ? fmt(r.metrics.deliveryLatencyMs.p95, 0) : '–'],
    ['Seed (s)', r => fmt(r.metrics.seedSeconds)], ['Tail (s)', r => fmt(r.metrics.tailSeconds)],
    ['Dupes', r => r.metrics.duplicates], ['FIFO viol.', r => r.metrics.fifoViolations],
  ];
  const head = table.createTHead().insertRow();
  cols.forEach(([h]) => { const th = document.createElement('th'); th.textContent = h; head.appendChild(th); });
  const body = table.createTBody();
  [...runs].reverse().forEach(r => { const row = body.insertRow(); cols.forEach(([, f]) => { row.insertCell().textContent = f(r); }); });
}

setup();
</script>
</body>
</html>
""";
}
