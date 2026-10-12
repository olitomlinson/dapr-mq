import { Link } from 'react-router-dom'
import { useSessions } from '../../api/hooks'
import { seg } from '../../api/client'
import { formatCount, formatTime } from '../../util/format'
import { QueryState, ui } from '../../components/ui'

export function SessionsTab({ queueId }: { queueId: string }) {
  const sessions = useSessions(queueId)
  const items = sessions.data?.pages.flatMap((p) => p.items) ?? []
  const nowSeconds = Date.now() / 1000

  return (
    <>
      <div className={ui.toolbar}>
        <span className={ui.spacer} />
        <button className={ui.button} onClick={() => sessions.refetch()} disabled={sessions.isFetching}>Refresh</button>
      </div>
      <QueryState isLoading={sessions.isLoading} error={sessions.error} />
      {sessions.data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead>
              <tr>
                <th>Session</th>
                <th className={ui.num}>Ready</th>
                <th className={ui.num}>Locked</th>
                <th>Lease</th>
              </tr>
            </thead>
            <tbody>
              {items.map((session) => (
                <tr key={session.sessionId}>
                  <td><Link to={`/queues/${seg(`${queueId}-session-${session.sessionId}`)}`}>{session.sessionId}</Link></td>
                  <td className={ui.num}>{formatCount(session.ready)}</td>
                  <td className={ui.num}>{formatCount(session.locked)}</td>
                  <td>
                    {session.leaseExpiresAt != null && session.leaseExpiresAt > nowSeconds
                      ? <span className={`${ui.badge} ${ui.badgeOk}`}>held until {formatTime(session.leaseExpiresAt)}</span>
                      : <span className={ui.muted}>free</span>}
                  </td>
                </tr>
              ))}
              {items.length === 0 && <tr><td colSpan={4} className={ui.muted}>No sessions.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      {sessions.hasNextPage && (
        <button className={ui.button} onClick={() => sessions.fetchNextPage()} disabled={sessions.isFetchingNextPage}>Load more ▸</button>
      )}
    </>
  )
}
