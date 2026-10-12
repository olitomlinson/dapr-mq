import { useState } from 'react'
import { useMessages, type ItemRef } from '../../api/hooks'
import { formatBytes, formatCount } from '../../util/format'
import { QueryState, ui } from '../../components/ui'
import { MessageDrawer } from '../../components/MessageDrawer'

/** Ready messages of one queue (the Dead-letter tab is this on {id}-deadletter). Manual refresh only. */
export function MessagesTab({ queueId, maxDeliveryCount }: { queueId: string; maxDeliveryCount: number }) {
  const [priority, setPriority] = useState<number | null>(null)
  const [open, setOpen] = useState<ItemRef | null>(null)
  const messages = useMessages(queueId, priority)
  const items = messages.data?.pages.flatMap((p) => p.items) ?? []

  return (
    <>
      <div className={ui.toolbar}>
        <select
          className={ui.select}
          aria-label="Priority"
          value={priority ?? ''}
          onChange={(e) => setPriority(e.target.value === '' ? null : Number(e.target.value))}
        >
          <option value="">All priorities</option>
          {[0, 1, 2, 3, 4, 5].map((p) => <option key={p} value={p}>Priority {p}</option>)}
        </select>
        <span className={`${ui.muted} ${ui.small}`}>
          {messages.dataUpdatedAt > 0 && `Snapshot at ${new Date(messages.dataUpdatedAt).toLocaleTimeString()} (items may have moved)`}
        </span>
        <span className={ui.spacer} />
        <button className={ui.button} onClick={() => messages.refetch()} disabled={messages.isFetching}>Refresh</button>
      </div>
      <QueryState isLoading={messages.isLoading} error={messages.error} notFound="Empty." />
      {messages.data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead>
              <tr>
                <th className={ui.num}>Seq</th>
                <th className={ui.num}>Pri</th>
                <th className={ui.num}>Deliveries</th>
                <th>Payload preview</th>
                <th className={ui.num}>Size</th>
              </tr>
            </thead>
            <tbody>
              {items.map((m) => (
                <tr
                  key={m.location + m.sequence}
                  className={ui.clickable}
                  onClick={() => setOpen({ type: 'message', queueId, location: m.location, sequence: m.sequence })}
                >
                  <td className={ui.num}>{formatCount(m.sequence)}</td>
                  <td className={ui.num}>{m.priority}</td>
                  <td className={ui.num}>{m.deliveryCount}{m.deliveryCount > 0 ? ` / ${maxDeliveryCount}` : ''}</td>
                  <td className={ui.preview}>{m.isBlobReference ? '📦 blob reference' : m.text}</td>
                  <td className={ui.num}>{formatBytes(m.isBlobReference && m.blobSize != null ? m.blobSize : m.sizeBytes)}</td>
                </tr>
              ))}
              {items.length === 0 && <tr><td colSpan={5} className={ui.muted}>No ready messages.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      {messages.hasNextPage && (
        <button className={ui.button} onClick={() => messages.fetchNextPage()} disabled={messages.isFetchingNextPage}>
          Load next 100 ▸
        </button>
      )}
      {open && <MessageDrawer item={open} maxDeliveryCount={maxDeliveryCount} onClose={() => setOpen(null)} />}
    </>
  )
}
