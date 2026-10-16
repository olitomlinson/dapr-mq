import { Link } from 'react-router-dom'
import { useOverview, useScaling } from '../api/hooks'
import { seg } from '../api/client'
import type { QueueDepth } from '../api/types'
import { formatCount } from '../util/format'
import { QueryState, Tile, ui } from '../components/ui'

export function OverviewPage() {
  const overview = useOverview()
  const scaledQueues = new Set(useScaling().data?.targets.map((t) => t.queueId)).size
  const o = overview.data
  const count = (n?: number) => (o ? formatCount(n ?? 0) : '–')
  const dlqsWithMessages = o?.deadLetterQueuesWithMessages ?? 0

  return (
    <div className={ui.page}>
      <h1 className={ui.title}>Overview</h1>
      <QueryState isLoading={overview.isLoading} error={overview.error} />
      <div className={ui.tiles}>
        <Tile label="Queues" value={count(o?.totals.Queue)} href="/queues" />
        <Tile label="Topics" value={count(o?.totals.Topic)} href="/topics" />
        <Tile label="Ready" value={count(o?.ready)} />
        <Tile label="Locked" value={count(o?.locked)} />
        {dlqsWithMessages > 0
          ? <Tile label="DLQs with messages ⚠" value={formatCount(dlqsWithMessages)} href="/queues?kind=DeadLetter" />
          : <Tile label="Dead-letter queues" value={count(o?.totals.DeadLetter)} href="/queues?kind=DeadLetter" />}
        <Tile label="HTTP sinks" value={count(o?.totals.Sink)} href="/sinks" />
        {scaledQueues > 0 && <Tile label="KEDA-scaled" value={formatCount(scaledQueues)} href="/scaling" />}
      </div>
      {o && (
        <div className={ui.grid2}>
          <QueueList title="Top queues by depth" rows={o.topQueues} empty="No queued messages." />
          <QueueList title="DLQs with messages" rows={o.deadLetterQueues} empty="No dead-lettered messages." deadLetter />
        </div>
      )}
      {o?.truncated && (
        <p className={`${ui.muted} ${ui.small}`}>Depth totals cover the first {formatCount(o.queuesRead)} queues only.</p>
      )}
      {!!o?.errors && (
        <p className={`${ui.muted} ${ui.small}`}>{formatCount(o.errors)} depth reads failed; those queues count as empty.</p>
      )}
      <p className={`${ui.muted} ${ui.small}`}>
        Read from committed state across every queue, so session items aren't included. Queues are listed once they first
        activate; browsing never consumes or locks a message.
      </p>
    </div>
  )
}

function QueueList({ title, rows, empty, deadLetter }: { title: string; rows: QueueDepth[]; empty: string; deadLetter?: boolean }) {
  return (
    <div className={ui.card}>
      <h2 className={ui.cardTitle}>{title}</h2>
      {rows.length === 0 ? <p className={ui.muted}>{empty}</p> : (
        <table className={ui.table}>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td><Link to={`/queues/${seg(r.id)}${deadLetter ? '?tab=deadletter' : ''}`}>{deadLetter ? `${r.id}-deadletter` : r.id}</Link></td>
                <td className={ui.num}>{formatCount(r.ready + r.locked)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
