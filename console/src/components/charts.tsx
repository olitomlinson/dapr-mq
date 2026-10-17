import { useState } from 'react'
import { scaleSymlog } from 'victory-vendor/d3-scale'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis, type TooltipProps } from 'recharts'
import s from './charts.module.css'

// Categorical series colors in fixed order (CSS tokens --series-1..4). Validated with the dataviz
// palette checker against both surfaces: adjacent CVD ΔE ≥ 8.4, normal-vision ΔE ≥ 19.8. Slots 3-4
// fall below 3:1 on the light surface, so every chart carries a legend with values (never color alone).
export const SERIES_COLORS = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--series-4)'] as const

/** A series' points as [unix seconds, value]; null is a gap (a failed read). */
export interface ChartLine {
  key: string
  label: string
  points: [number, number | null][]
}

const number = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 })
const compact = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 })

/**
 * One measure over time: one y-axis, 2px lines, a crosshair tooltip, and a legend that carries
 * each series' latest value (≤ 4 series, colored in fixed order). One series needs no legend box:
 * the title names it, and its latest value sits beside the title. A toggle puts the y-axis on a
 * symlog scale (log, but linear through 0, since replicas and backlogs often sit at 0).
 */
export function TimeSeriesChart({ title, lines, unit, height = 200 }: { title: string; lines: ChartLine[]; unit?: string; height?: number }) {
  const times = [...new Set(lines.flatMap((l) => l.points.map(([t]) => t)))].sort((a, b) => a - b)
  const byKey = lines.map((l) => new Map(l.points))
  const data = times.map((t) => Object.fromEntries([['t', t], ...lines.map((l, i) => [l.key, byKey[i].get(t) ?? null])]))
  const latest = (l: ChartLine) => l.points[l.points.length - 1]?.[1] ?? undefined
  const format = (v: number | undefined) => (v === undefined ? '–' : `${number.format(v)}${unit ? ` ${unit}` : ''}`)
  const [log, setLog] = useState(false)
  const ticks = log ? logTicks(Math.max(0, ...lines.flatMap((l) => l.points.map(([, v]) => v ?? 0)))) : undefined

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
                <span>{l.label}</span> <span className={s.legendValue}>{format(latest(l))}</span>
              </span>
            ))}
          </span>
        )}
        <button type="button" className={`${s.range} ${s.scaleToggle}`} aria-pressed={log} onClick={() => setLog(!log)}>Log scale</button>
      </figcaption>
      {data.length === 0 ? (
        <div className={s.empty} style={{ height }}>No samples yet.</div>
      ) : (
        <div style={{ width: '100%', height }} role="img" aria-label={`${title} chart`}>
          <ResponsiveContainer>
            <LineChart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid vertical={false} stroke="var(--grid)" strokeWidth={1} />
              <XAxis dataKey="t" type="number" domain={['dataMin', 'dataMax']} tickFormatter={clock} stroke="var(--grid)"
                tick={{ fill: 'var(--text-muted)', fontSize: 11 }} tickLine={false} minTickGap={40} />
              <YAxis width={44} {...(ticks && { scale: scaleSymlog(), domain: [0, ticks[ticks.length - 1]], ticks })} tickFormatter={(v) => compact.format(v)} stroke="var(--grid)" axisLine={false} tickLine={false}
                tick={{ fill: 'var(--text-muted)', fontSize: 11 }} allowDecimals={false} />
              <Tooltip content={<ChartTooltip lines={lines} format={format} />} cursor={{ stroke: 'var(--text-muted)', strokeWidth: 1 }} />
              {lines.map((l, i) => (
                // Step lines: replica counts and samples hold until the next sample.
                <Line key={l.key} dataKey={l.key} name={l.label} type="stepAfter" stroke={SERIES_COLORS[i]} strokeWidth={2}
                  strokeLinecap="round" strokeLinejoin="round" dot={false} isAnimationActive={false}
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

/** Log-scale y ticks: 0, then each power of ten up to the first at or above max. */
export function logTicks(max: number): number[] {
  const ticks = [0, 1]
  while (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] * 10)
  return ticks
}

/** A 1-series trend for a table cell: no axes or labels; the row's numbers carry the values. */
export function Sparkline({ points, label }: { points: [number, number | null][]; label: string }) {
  if (points.filter(([, v]) => v !== null).length < 2) return <span aria-label={`${label}: no data`}>–</span>
  return (
    <span style={{ display: 'inline-block', width: 96, height: 22 }} role="img" aria-label={label}>
      <ResponsiveContainer>
        <LineChart data={points.map(([t, v]) => ({ t, v }))} margin={{ top: 2, right: 2, bottom: 2, left: 2 }}>
          <YAxis hide domain={['dataMin', 'dataMax']} />
          <Line dataKey="v" type="stepAfter" stroke="var(--series-1)" strokeWidth={1.5} dot={false} isAnimationActive={false} />
        </LineChart>
      </ResponsiveContainer>
    </span>
  )
}

const clock = (unixSeconds: number) =>
  new Date(unixSeconds * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
