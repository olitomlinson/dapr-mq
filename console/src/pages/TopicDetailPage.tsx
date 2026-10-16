import { Link, useParams } from 'react-router-dom'
import { useScaling, useTopic } from '../api/hooks'
import { seg } from '../api/client'
import type { CircuitBreaker, ScalingTarget } from '../api/types'
import { formatCount, formatTime } from '../util/format'
import { replicasNow } from '../util/scaling'
import { QueryState, ui } from '../components/ui'

export function TopicDetailPage() {
  const topicId = useParams().topicId!
  const topic = useTopic(topicId)
  const t = topic.data
  const scaling = useScaling().data

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
                <tr><th>Subscriber</th><th>Queue</th><th className={ui.num}>Lag</th><th>Breaker</th><th className={ui.num}>Consumers</th></tr>
              </thead>
              <tbody>
                {t.subscribers.map((sub) => (
                  <tr key={sub.subscriberId}>
                    <td>{sub.subscriberId}</td>
                    <td><Link to={`/queues/${seg(sub.queueId)}`}>{sub.queueId} ↗</Link></td>
                    <td className={ui.num}>{formatCount(sub.lag)}</td>
                    <td><Breaker breaker={sub.circuitBreaker} /></td>
                    <td className={ui.num}><Consumers queueId={sub.queueId} targets={scaling?.targets} /></td>
                  </tr>
                ))}
                {t.subscribers.length === 0 && <tr><td colSpan={5} className={ui.muted}>No subscribers.</td></tr>}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}

/** Replicas a KEDA-scaled subscription runs (or its metric asks for); – when KEDA doesn't scale it. */
function Consumers({ queueId, targets }: { queueId: string; targets?: ScalingTarget[] }) {
  const target = targets?.find((t) => t.queueId === queueId)
  const replicas = target ? replicasNow(target) : null
  if (!target || replicas === null) return <span className={ui.muted}>–</span>
  return <Link to={`/queues/${seg(queueId)}?tab=scaling`}>{formatCount(replicas)} {replicas === 1 ? 'replica' : 'replicas'}</Link>
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
