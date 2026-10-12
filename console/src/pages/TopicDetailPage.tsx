import { Link, useParams } from 'react-router-dom'
import { useTopic } from '../api/hooks'
import { seg } from '../api/client'
import type { CircuitBreaker } from '../api/types'
import { formatCount, formatTime } from '../util/format'
import { QueryState, ui } from '../components/ui'

export function TopicDetailPage() {
  const topicId = useParams().topicId!
  const topic = useTopic(topicId)
  const t = topic.data

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
                <tr><th>Subscriber</th><th>Queue</th><th className={ui.num}>Lag</th><th>Breaker</th></tr>
              </thead>
              <tbody>
                {t.subscribers.map((sub) => (
                  <tr key={sub.subscriberId}>
                    <td>{sub.subscriberId}</td>
                    <td><Link to={`/queues/${seg(sub.queueId)}`}>{sub.queueId} ↗</Link></td>
                    <td className={ui.num}>{formatCount(sub.lag)}</td>
                    <td><Breaker breaker={sub.circuitBreaker} /></td>
                  </tr>
                ))}
                {t.subscribers.length === 0 && <tr><td colSpan={4} className={ui.muted}>No subscribers.</td></tr>}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}

function Breaker({ breaker }: { breaker?: CircuitBreaker | null }) {
  if (!breaker) return <span className={`${ui.badge} ${ui.badgeOk}`}>● closed</span>
  if (breaker.blacklisted) return <span className={`${ui.badge} ${ui.badgeDanger}`}>blacklisted ({breaker.consecutiveFailures} errors)</span>
  return (
    <span className={`${ui.badge} ${ui.badgeWarn}`}>
      ○ open ({breaker.consecutiveFailures} errors{breaker.nextRetryAt != null ? `, retry ${formatTime(breaker.nextRetryAt)}` : ''})
    </span>
  )
}
