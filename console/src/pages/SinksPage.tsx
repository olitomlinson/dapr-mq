import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQueries } from '@tanstack/react-query'
import { useCatalog } from '../api/hooks'
import { getJson, seg } from '../api/client'
import { usePollInterval } from '../api/refresh'
import type { QueueDetail } from '../api/types'
import { QueryState, ui } from '../components/ui'

const PAGE = 50

export function SinksPage() {
  const [cursors, setCursors] = useState<(string | null)[]>([null])
  const catalog = useCatalog('Sink', cursors[cursors.length - 1], PAGE)
  const refetchInterval = usePollInterval()
  const sinks = catalog.data?.entries ?? []
  // A sink's config is part of its queue's detail (cached by the operator for 10 s).
  const details = useQueries({
    queries: sinks.map((sink) => ({
      queryKey: ['queue', sink.parentId],
      queryFn: () => getJson<QueueDetail>(`/queues/${seg(sink.parentId ?? '')}`),
      enabled: !!sink.parentId,
      refetchInterval,
    })),
  })

  return (
    <div className={ui.page}>
      <h1 className={ui.title}>HTTP sinks</h1>
      <QueryState isLoading={catalog.isLoading} error={catalog.error} />
      {catalog.data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead>
              <tr><th>Queue</th><th>URL</th><th className={ui.num}>Max concurrency</th><th className={ui.num}>Lock TTL</th><th className={ui.num}>Polling interval</th></tr>
            </thead>
            <tbody>
              {sinks.map((sink, i) => {
                const config = details[i]?.data?.sink
                return (
                  <tr key={sink.id}>
                    <td><Link to={`/queues/${seg(sink.parentId ?? '')}`}>{sink.parentId}</Link></td>
                    <td>{config ? config.url : <span className={ui.muted}>{details[i]?.isLoading ? '…' : 'not initialised'}</span>}</td>
                    <td className={ui.num}>{config?.maxConcurrency ?? '–'}</td>
                    <td className={ui.num}>{config ? `${config.lockTtlSeconds}s` : '–'}</td>
                    <td className={ui.num}>{config ? `${config.currentIntervalSeconds}s` : '–'}</td>
                  </tr>
                )
              })}
              {sinks.length === 0 && <tr><td colSpan={5} className={ui.muted}>No HTTP sinks.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      <div className={ui.toolbar}>
        <button className={ui.button} disabled={cursors.length === 1} onClick={() => setCursors(cursors.slice(0, -1))}>‹ Previous</button>
        <button className={ui.button} disabled={!catalog.data?.nextCursor} onClick={() => setCursors([...cursors, catalog.data!.nextCursor!])}>Next ›</button>
      </div>
    </div>
  )
}
