import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useMetrics, useQueuePage } from '../api/hooks'
import { seg } from '../api/client'
import type { MetricSeries, QueueRow } from '../api/types'
import { formatCount, subscriptionTopic } from '../util/format'
import { QueryState, ui } from '../components/ui'
import { Sparkline } from '../components/charts'

export function QueuesPage() {
  const [params, setParams] = useSearchParams()
  const kind = params.get('kind') === 'DeadLetter' ? 'DeadLetter' : 'Queue'
  // Cursors of the pages before this one, so Previous can step back.
  const [cursors, setCursors] = useState<(string | null)[]>([null])
  const cursor = cursors[cursors.length - 1]
  const [filter, setFilter] = useState('')
  const [hideEmpty, setHideEmpty] = useState(false)
  const [hideSubs, setHideSubs] = useState(false)
  const page = useQueuePage(kind, cursor)
  // Rates and depth history for the rows on this page (absent without Prometheus).
  const ids = page.data?.items.map((row) => row.id) ?? []
  const metrics = useMetrics('queue-list', { ids }, ids.length > 0)
  const hasMetrics = !!metrics.data

  const setKind = (next: string) => {
    setCursors([null])
    setParams(next === 'Queue' ? {} : { kind: next })
  }

  const rows = (page.data?.items ?? []).filter(
    (row) =>
      row.id.toLowerCase().includes(filter.toLowerCase()) &&
      !(hideEmpty && row.ready + row.locked + (row.deadLetterCount ?? 0) === 0) &&
      !(hideSubs && subscriptionTopic(row.id)),
  )
  const total = page.data?.totals?.[kind]

  return (
    <div className={ui.page}>
      <div className={ui.pageHeader}>
        <h1 className={ui.title}>Queues</h1>
        {total !== undefined && <span className={ui.badge}>{formatCount(total)}</span>}
      </div>
      <div className={ui.toolbar}>
        <input className={ui.input} placeholder="Filter this page…" aria-label="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} />
        <select className={ui.select} aria-label="Kind" value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="Queue">Queues</option>
          <option value="DeadLetter">Dead-letter queues</option>
        </select>
        <label><input type="checkbox" checked={hideEmpty} onChange={(e) => setHideEmpty(e.target.checked)} /> Hide empty</label>
        <label><input type="checkbox" checked={hideSubs} onChange={(e) => setHideSubs(e.target.checked)} /> Hide subscriptions</label>
      </div>
      <QueryState isLoading={page.isLoading} error={page.error} />
      {page.data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead>
              <tr>
                <th>Queue</th>
                <th>Kind</th>
                <th className={ui.num}>Ready</th>
                <th className={ui.num}>Locked</th>
                {kind === 'Queue' && <th className={ui.num}>DLQ</th>}
                {hasMetrics && <><th className={ui.num}>In/s</th><th className={ui.num}>Out/s</th><th>Depth (1h)</th></>}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => <Row key={row.id} row={row} metrics={hasMetrics ? metrics.data!.series : null} />)}
              {rows.length === 0 && (
                <tr><td colSpan={8} className={ui.muted}>No queues{filter || hideEmpty || hideSubs ? ' match' : ''}.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}
      <div className={ui.toolbar}>
        <button className={ui.button} disabled={cursors.length === 1} onClick={() => setCursors(cursors.slice(0, -1))}>‹ Previous</button>
        <button className={ui.button} disabled={!page.data?.nextCursor} onClick={() => setCursors([...cursors, page.data!.nextCursor!])}>Next ›</button>
        <span className={`${ui.muted} ${ui.small}`}>Page {cursors.length}</span>
      </div>
    </div>
  )
}

const rate = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 })

function Row({ row, metrics }: { row: QueueRow; metrics: MetricSeries[] | null }) {
  const forRow = (name: string) => metrics?.find((x) => x.name === name && x.labels.queue === row.id)
  const instant = (name: string) => rate.format(forRow(name)?.points[0]?.[1] ?? 0)
  const topic = subscriptionTopic(row.id)
  const dlq = row.deadLetterCount ?? 0
  return (
    <tr>
      <td>
        <Link to={`/queues/${seg(row.id)}`}>{row.id}</Link>
        {row.error && <span className={`${ui.badge} ${ui.badgeDanger}`} title={row.error}> depth unavailable</span>}
      </td>
      <td>
        {row.kind === 'DeadLetter' ? (
          <span>dlq of <Link to={`/queues/${seg(row.parentId ?? '')}`}>{row.parentId}</Link></span>
        ) : topic ? (
          <Link to={`/topics/${seg(topic)}`}>sub ↗</Link>
        ) : (
          'queue'
        )}
      </td>
      <td className={ui.num}>{formatCount(row.ready)}</td>
      <td className={ui.num}>{formatCount(row.locked)}</td>
      {row.kind === 'Queue' && (
        <td className={ui.num}>
          {dlq > 0 ? <Link to={`/queues/${seg(row.id)}?tab=deadletter`} className={`${ui.badge} ${ui.badgeWarn}`}>{formatCount(dlq)}</Link> : '0'}
        </td>
      )}
      {metrics && (
        <>
          <td className={ui.num}>{instant('in')}</td>
          <td className={ui.num}>{instant('out')}</td>
          <td><Sparkline points={forRow('depth')?.points ?? []} label={`${row.id} depth over the last hour`} /></td>
        </>
      )}
    </tr>
  )
}
