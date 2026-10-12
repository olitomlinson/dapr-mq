import { Link } from 'react-router-dom'
import type { QueueDetail } from '../../api/types'
import { formatCount, formatTime } from '../../util/format'
import { ui } from '../../components/ui'

export function OverviewTab({ detail: d }: { detail: QueueDetail }) {
  const max = Math.max(1, ...d.priorities.map((p) => p.count))
  return (
    <div className={ui.grid2}>
      <div className={ui.card}>
        <h2 className={ui.cardTitle}>Configuration</h2>
        <dl className={ui.kv}>
          <dt>Segment size</dt><dd>{d.segmentSize}</dd>
          <dt>Dedup</dt><dd>{d.dedupEnabled ? 'on' : 'off'}</dd>
          <dt>Max delivery count</dt><dd>{d.maxDeliveryCount} (global)</dd>
          <dt>Next sequence</dt><dd>{formatCount(d.nextSequence)}</dd>
          {d.sessionLeaseExpiresAt != null && (<><dt>Session lease</dt><dd>until {formatTime(d.sessionLeaseExpiresAt)}</dd></>)}
        </dl>
      </div>
      <div className={ui.card}>
        <h2 className={ui.cardTitle}>Ready by priority</h2>
        {d.priorities.length === 0 && <p className={ui.muted}>Empty.</p>}
        {d.priorities.map((p) => (
          <div key={p.priority} className={ui.barRow}>
            <span>P{p.priority}{p.priority === 0 ? ' (fast)' : ''}</span>
            <div className={ui.barTrack}><div className={ui.barFill} style={{ width: `${(p.count / max) * 100}%` }} /></div>
            <span className={ui.num}>{formatCount(p.count)}</span>
          </div>
        ))}
        <p className={ui.small}>
          Locked: {formatCount(d.locked)} · DLQ: {formatCount(d.deadLetterReady + d.deadLetterLocked)}
          {d.sessionCount > 0 && <> · Sessions: {formatCount(d.sessionCount)}</>}
        </p>
      </div>
      {d.sink && (
        <div className={ui.card}>
          <h2 className={ui.cardTitle}>HTTP sink</h2>
          <dl className={ui.kv}>
            <dt>URL</dt><dd>{d.sink.url}</dd>
            <dt>Max concurrency</dt><dd>{d.sink.maxConcurrency}</dd>
            <dt>Lock TTL</dt><dd>{d.sink.lockTtlSeconds}s</dd>
            <dt>Polling interval</dt><dd>{d.sink.currentIntervalSeconds}s</dd>
          </dl>
          <p className={ui.small}><Link to="/sinks">All sinks</Link></p>
        </div>
      )}
    </div>
  )
}
