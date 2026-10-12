import { Link, useParams, useSearchParams } from 'react-router-dom'
import { useQueueDetail } from '../api/hooks'
import { formatCount } from '../util/format'
import { QueryState, Tabs, ui } from '../components/ui'
import { OverviewTab } from './queue/OverviewTab'
import { MessagesTab } from './queue/MessagesTab'
import { LockedTab } from './queue/LockedTab'
import { SessionsTab } from './queue/SessionsTab'

type Tab = 'overview' | 'messages' | 'locked' | 'deadletter' | 'sessions'

export function QueueDetailPage() {
  const queueId = useParams().queueId!
  const [params, setParams] = useSearchParams()
  const detail = useQueueDetail(queueId)
  const isDeadLetter = queueId.endsWith('-deadletter')
  const d = detail.data

  const tabs: { id: Tab; label: string }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'messages', label: `Messages${d ? ` ${formatCount(d.ready)}` : ''}` },
    { id: 'locked', label: `Locked${d ? ` ${formatCount(d.locked)}` : ''}` },
    ...(isDeadLetter ? [] : [{ id: 'deadletter' as Tab, label: `Dead-letter${d ? ` ${formatCount(d.deadLetterReady + d.deadLetterLocked)}` : ''}` }]),
    { id: 'sessions', label: `Sessions${d?.sessionCount ? ` ${formatCount(d.sessionCount)}` : ''}` },
  ]
  const requested = params.get('tab') as Tab | null
  const tab: Tab = tabs.some((t) => t.id === requested) ? requested! : 'overview'

  return (
    <div className={ui.page}>
      <div>
        <div className={ui.crumbs}><Link to="/queues">Queues</Link> /</div>
        <h1 className={ui.title}>{queueId}</h1>
      </div>
      {d?.errorMessage && (
        <div className={`${ui.banner} ${ui.bannerDanger}`} role="alert"><strong>Corrupt:</strong> {d.errorMessage}</div>
      )}
      <QueryState isLoading={detail.isLoading} error={detail.error} notFound="No such queue. Nothing has been enqueued to it." />
      {d && (
        <>
          <Tabs tabs={tabs} value={tab} onChange={(next) => setParams(next === 'overview' ? {} : { tab: next })} />
          {tab === 'overview' && <OverviewTab detail={d} />}
          {tab === 'messages' && <MessagesTab key={queueId} queueId={queueId} maxDeliveryCount={d.maxDeliveryCount} />}
          {tab === 'locked' && <LockedTab queueId={queueId} maxDeliveryCount={d.maxDeliveryCount} />}
          {tab === 'deadletter' && <MessagesTab key={`${queueId}-deadletter`} queueId={`${queueId}-deadletter`} maxDeliveryCount={d.maxDeliveryCount} />}
          {tab === 'sessions' && <SessionsTab queueId={queueId} />}
        </>
      )}
    </div>
  )
}
