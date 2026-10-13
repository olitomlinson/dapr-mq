import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useCatalog, useMetrics } from '../api/hooks'
import { seg } from '../api/client'
import type { MetricSeries, MetricsRange } from '../api/types'
import { formatCount } from '../util/format'
import { QueryState, Tile, ui } from '../components/ui'
import { MetricsState, RangePicker, TimeSeriesChart, seriesPoints } from '../components/charts'

export function OverviewPage() {
  const [range, setRange] = useState<MetricsRange>('1h')
  const catalog = useCatalog(null, null, 1)
  const depth = useMetrics('depth-summary', {})
  const throughput = useMetrics('throughput', { range })
  const totals = catalog.data?.totals ?? {}
  const count = (n?: number) => (catalog.data ? formatCount(n ?? 0) : '–')
  const depthTotal = (state: string) => {
    const point = depth.data?.series.find((x) => x.name === 'total' && x.labels.state === state)?.points[0]
    return depth.data ? formatCount(Math.round(point?.[1] ?? 0)) : '–'
  }
  const dlqs = depth.data?.series.filter((x) => x.name === 'dlq') ?? []

  return (
    <div className={ui.page}>
      <h1 className={ui.title}>Overview</h1>
      <QueryState isLoading={catalog.isLoading} error={catalog.error} />
      <div className={ui.tiles}>
        <Tile label="Queues" value={count(totals.Queue)} href="/queues" />
        <Tile label="Topics" value={count(totals.Topic)} href="/topics" />
        {!depth.error && <Tile label="Ready" value={depthTotal('ready')} />}
        {!depth.error && <Tile label="Locked" value={depthTotal('locked')} />}
        <Tile label={dlqs.length > 0 ? 'DLQs with messages ⚠' : 'Dead-letter queues'} value={dlqs.length > 0 ? formatCount(dlqs.length) : count(totals.DeadLetter)} href="/queues?kind=DeadLetter" />
        <Tile label="HTTP sinks" value={count(totals.Sink)} href="/sinks" />
      </div>
      <div className={ui.card}>
        <div className={ui.toolbar} style={{ marginBottom: 8 }}>
          <span className={ui.spacer} style={{ flex: 1 }} />
          <RangePicker value={range} onChange={setRange} />
        </div>
        <MetricsState error={throughput.error}>
          <TimeSeriesChart
            title="Throughput"
            unit="msg/s"
            lines={[
              { key: 'enqueued', label: 'Enqueued', points: seriesPoints(throughput.data?.series, 'enqueued') },
              { key: 'dequeued', label: 'Dequeued', points: seriesPoints(throughput.data?.series, 'dequeued') },
              { key: 'deadlettered', label: 'Dead-lettered', points: seriesPoints(throughput.data?.series, 'deadlettered') },
            ]}
          />
        </MetricsState>
      </div>
      {!depth.error && (
        <div className={ui.grid2}>
          <QueueList title="Top queues by depth" series={depth.data?.series.filter((x) => x.name === 'top') ?? []} empty="No queued messages." />
          <QueueList title="DLQs with messages" series={dlqs} empty="No dead-lettered messages." deadLetter />
        </div>
      )}
      <p className={`${ui.muted} ${ui.small}`}>
        Queues are listed once they first activate. Everything here is read from committed state; browsing never consumes or locks a message.
      </p>
    </div>
  )
}

function QueueList({ title, series, empty, deadLetter }: { title: string; series: MetricSeries[]; empty: string; deadLetter?: boolean }) {
  const rows = series
    .map((x) => ({ queue: x.labels.queue, value: x.points[0]?.[1] ?? 0 }))
    .filter((r) => r.queue)
    .sort((a, b) => b.value - a.value)
  return (
    <div className={ui.card}>
      <h2 className={ui.cardTitle}>{title}</h2>
      {rows.length === 0 ? <p className={ui.muted}>{empty}</p> : (
        <table className={ui.table}>
          <tbody>
            {rows.map((r) => (
              <tr key={r.queue}>
                <td><Link to={`/queues/${seg(r.queue)}${deadLetter ? '?tab=deadletter' : ''}`}>{deadLetter ? `${r.queue}-deadletter` : r.queue}</Link></td>
                <td className={ui.num}>{formatCount(Math.round(r.value))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
