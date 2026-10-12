import type { ReactNode } from 'react'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis, type TooltipProps } from 'recharts'
import { ApiError } from '../api/client'
import type { MetricSeries, MetricsRange } from '../api/types'
import s from './charts.module.css'

// Categorical series colors in fixed order (CSS tokens --series-1..4). Validated with the dataviz
// palette checker against both surfaces: adjacent CVD ΔE ≥ 8.4, normal-vision ΔE ≥ 19.8. Slots 3-4
// fall below 3:1 on the light surface, so every chart carries a legend with values (never color alone).
export const SERIES_COLORS = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--series-4)'] as const

export interface ChartLine {
  key: string
  label: string
  points: [number, number][]
}

const number = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 })
const compact = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 })

/** Points of the series named `name` (summed across label sets when there are several). */
export function seriesPoints(series: MetricSeries[] | undefined, name: string): [number, number][] {
  const matching = (series ?? []).filter((x) => x.name === name)
  if (matching.length <= 1) return matching[0]?.points.map(([t, v]) => [t, finite(v)]) ?? []
  const sums = new Map<number, number>()
  for (const x of matching) for (const [t, v] of x.points) sums.set(t, (sums.get(t) ?? 0) + finite(v))
  return [...sums.entries()].sort((a, b) => a[0] - b[0])
}

const finite = (v: number) => (Number.isFinite(v) ? v : 0)

/**
 * One measure over time: one y-axis, 2px lines, a crosshair tooltip, and a legend that carries
 * each series' latest value (≤ 4 series, colored in fixed order). One series needs no legend box:
 * the title names it, and its latest value sits beside the title.
 */
export function TimeSeriesChart({ title, lines, unit, height = 200 }: { title: string; lines: ChartLine[]; unit?: string; height?: number }) {
  const times = [...new Set(lines.flatMap((l) => l.points.map(([t]) => t)))].sort((a, b) => a - b)
  const byKey = lines.map((l) => new Map(l.points))
  const data = times.map((t) => Object.fromEntries([['t', t], ...lines.map((l, i) => [l.key, byKey[i].get(t) ?? null])]))
  const latest = (l: ChartLine) => l.points[l.points.length - 1]?.[1]
  const format = (v: number | undefined) => (v === undefined ? '–' : `${number.format(v)}${unit ? ` ${unit}` : ''}`)

  return (
    <figure className={s.chart} style={{ margin: 0 }}>
      <figcaption className={s.head}>
        <h3 className={s.title}>{title}</h3>
        {lines.length === 1 && <span className={s.legendValue}>{format(latest(lines[0]))}</span>}
        {lines.length > 1 && (
          <span className={s.legend}>
            {lines.map((l, i) => (
              <span key={l.key} className={s.legendItem}>
                <span className={s.key} style={{ background: SERIES_COLORS[i] }} />
                {l.label} <span className={s.legendValue}>{format(latest(l))}</span>
              </span>
            ))}
          </span>
        )}
      </figcaption>
      {data.length === 0 ? (
        <div className={s.empty} style={{ height }}>No data in this range.</div>
      ) : (
        <div style={{ width: '100%', height }} role="img" aria-label={`${title} chart`}>
          <ResponsiveContainer>
            <LineChart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid vertical={false} stroke="var(--grid)" strokeWidth={1} />
              <XAxis dataKey="t" type="number" domain={['dataMin', 'dataMax']} tickFormatter={clock} stroke="var(--grid)"
                tick={{ fill: 'var(--text-muted)', fontSize: 11 }} tickLine={false} minTickGap={40} />
              <YAxis width={44} tickFormatter={(v) => compact.format(v)} stroke="var(--grid)" axisLine={false} tickLine={false}
                tick={{ fill: 'var(--text-muted)', fontSize: 11 }} allowDecimals={false} />
              <Tooltip content={<ChartTooltip lines={lines} format={format} />} cursor={{ stroke: 'var(--text-muted)', strokeWidth: 1 }} />
              {lines.map((l, i) => (
                <Line key={l.key} dataKey={l.key} name={l.label} type="monotone" stroke={SERIES_COLORS[i]} strokeWidth={2}
                  strokeLinecap="round" strokeLinejoin="round" dot={false} isAnimationActive={false} connectNulls
                  activeDot={{ r: 4, fill: SERIES_COLORS[i], stroke: 'var(--surface)', strokeWidth: 2 }} />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}
    </figure>
  )
}

function ChartTooltip({ active, payload, label, lines, format }: TooltipProps<number, string> & { lines: ChartLine[]; format: (v?: number) => string }) {
  if (!active || !payload?.length) return null
  return (
    <div className={s.tooltip}>
      <div className={s.tooltipTime}>{clock(label as number)}</div>
      {lines.map((l, i) => {
        const value = payload.find((p) => p.dataKey === l.key)?.value
        return (
          <div key={l.key} className={s.tooltipRow}>
            <span className={s.key} style={{ background: SERIES_COLORS[i] }} />
            {l.label}
            <strong>{format(value ?? undefined)}</strong>
          </div>
        )
      })}
    </div>
  )
}

/** A 1-series trend for a table cell: no axes or labels; the row's numbers carry the values. */
export function Sparkline({ points, label }: { points: [number, number][]; label: string }) {
  if (points.length < 2) return <span aria-label={`${label}: no data`}>–</span>
  return (
    <span style={{ display: 'inline-block', width: 96, height: 22 }} role="img" aria-label={label}>
      <ResponsiveContainer>
        <LineChart data={points.map(([t, v]) => ({ t, v }))} margin={{ top: 2, right: 2, bottom: 2, left: 2 }}>
          <YAxis hide domain={['dataMin', 'dataMax']} />
          <Line dataKey="v" stroke="var(--series-1)" strokeWidth={1.5} dot={false} isAnimationActive={false} />
        </LineChart>
      </ResponsiveContainer>
    </span>
  )
}

export function RangePicker({ value, onChange }: { value: MetricsRange; onChange: (range: MetricsRange) => void }) {
  return (
    <span className={s.ranges} role="group" aria-label="Time range">
      {(['15m', '1h', '6h', '24h'] as MetricsRange[]).map((r) => (
        <button key={r} className={s.range} aria-pressed={r === value} onClick={() => onChange(r)}>{r}</button>
      ))}
    </span>
  )
}

/** Explains a metrics query that didn't return data; null when it did. */
export function MetricsState({ error, children }: { error: unknown; children?: ReactNode }) {
  if (error instanceof ApiError && error.status === 501) {
    return <p className={s.empty} style={{ height: 'auto', padding: 16 }}>Metrics not configured. Set PROMETHEUS_URL on the operator to see charts.</p>
  }
  if (error) return <p className={s.empty} style={{ height: 'auto', padding: 16 }}>Prometheus unavailable.</p>
  return <>{children}</>
}

const clock = (unixSeconds: number) =>
  new Date(unixSeconds * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
