import { Link, useParams } from 'react-router-dom'
import { useState } from 'react'
import { useMetrics, useTopic } from '../api/hooks'
import { seg } from '../api/client'
import type { CircuitBreaker, MetricsRange } from '../api/types'
import { formatCount, formatTime } from '../util/format'
import { QueryState, ui } from '../components/ui'
import { MetricsState, RangePicker, TimeSeriesChart, seriesPoints } from '../components/charts'

export function TopicDetailPage() {
  const topicId = useParams().topicId!
  const topic = useTopic(topicId)
  const [range, setRange] = useState<MetricsRange>('1h')
  const metrics = useMetrics('topic', { id: topicId, range })
  const t = topic.data
  const dispatchRate = (subscriberId: string) => {
    const points = metrics.data?.series.find((x) => x.name === 'dispatched' && x.labels.subscriber === subscriberId)?.points
    return points?.length ? rate.format(points[points.length - 1][1]) : '–'
  }

  return (
    <div className={ui.page}>
      <div>
        <div className={ui.crumbs}><Link to="/topics">Topics</Link> /</div>
        <h1 className={ui.title}>{topicId}</h1>
      </div>
      <QueryState isLoading={topic.isLoading} error={topic.error} notFound="No such topic." />
      {t && (
        <>
          <p className={ui.muted}>
            Generation {t.currentGeneration} · item log: {formatCount(t.itemLogCount)} · publishes pending: {formatCount(t.pendingPublishCount)}
          </p>
          <div className={ui.tableWrap}>
            <table className={ui.table}>
              <thead>
                <tr><th>Subscriber</th><th>Queue</th><th className={ui.num}>Lag</th><th className={ui.num}>Dispatched/s</th><th>Breaker</th></tr>
              </thead>
              <tbody>
                {t.subscribers.map((sub) => (
                  <tr key={sub.subscriberId}>
                    <td>{sub.subscriberId}</td>
                    <td><Link to={`/queues/${seg(sub.queueId)}`}>{sub.queueId} ↗</Link></td>
                    <td className={ui.num}>{formatCount(sub.lag)}</td>
                    <td className={ui.num}>{dispatchRate(sub.subscriberId)}</td>
                    <td><Breaker breaker={sub.circuitBreaker} /></td>
                  </tr>
                ))}
                {t.subscribers.length === 0 && <tr><td colSpan={5} className={ui.muted}>No subscribers.</td></tr>}
              </tbody>
            </table>
          </div>
          <div className={ui.card}>
            <div className={ui.toolbar} style={{ marginBottom: 8 }}>
              <span style={{ flex: 1 }} />
              <RangePicker value={range} onChange={setRange} />
            </div>
            <MetricsState error={metrics.error}>
              <TimeSeriesChart title="Publish vs dispatch" unit="msg/s" lines={[
                { key: 'published', label: 'Published', points: seriesPoints(metrics.data?.series, 'published') },
                { key: 'dispatched', label: 'Dispatched (all subscribers)', points: seriesPoints(metrics.data?.series, 'dispatched') },
              ]} />
            </MetricsState>
          </div>
        </>
      )}
    </div>
  )
}

const rate = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 })

function Breaker({ breaker }: { breaker?: CircuitBreaker | null }) {
  if (!breaker) return <span className={`${ui.badge} ${ui.badgeOk}`}>● closed</span>
  if (breaker.blacklisted) return <span className={`${ui.badge} ${ui.badgeDanger}`}>blacklisted ({breaker.consecutiveFailures} errors)</span>
  return (
    <span className={`${ui.badge} ${ui.badgeWarn}`}>
      ○ open ({breaker.consecutiveFailures} errors{breaker.nextRetryAt != null ? `, retry ${formatTime(breaker.nextRetryAt)}` : ''})
    </span>
  )
}
