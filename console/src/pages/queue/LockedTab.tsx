import { useEffect, useState } from 'react'
import { useLocks, type ItemRef } from '../../api/hooks'
import type { Lock } from '../../api/types'
import { formatCount, formatTimeLeft } from '../../util/format'
import { QueryState, ui } from '../../components/ui'
import { MessageDrawer } from '../../components/MessageDrawer'

/** Outstanding locks with live countdowns. The list itself is a manual-refresh snapshot. */
export function LockedTab({ queueId, maxDeliveryCount }: { queueId: string; maxDeliveryCount: number }) {
  const locks = useLocks(queueId)
  const [open, setOpen] = useState<ItemRef | null>(null)
  const now = useNow()
  const items = locks.data?.pages.flatMap((p) => p.items) ?? []

  return (
    <>
      <div className={ui.toolbar}>
        <span className={`${ui.muted} ${ui.small}`}>
          {locks.dataUpdatedAt > 0 && `Snapshot at ${new Date(locks.dataUpdatedAt).toLocaleTimeString()}`}
        </span>
        <span className={ui.spacer} />
        <button className={ui.button} onClick={() => locks.refetch()} disabled={locks.isFetching}>Refresh</button>
      </div>
      <QueryState isLoading={locks.isLoading} error={locks.error} />
      {locks.data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead>
              <tr>
                <th>Lock id</th>
                <th className={ui.num}>Seq</th>
                <th className={ui.num}>Pri</th>
                <th className={ui.num}>Deliveries</th>
                <th>Expires in</th>
                <th>Mode</th>
                <th>Preview</th>
              </tr>
            </thead>
            <tbody>
              {items.map((lock) => (
                <tr key={lock.lockId} className={ui.clickable} onClick={() => setOpen({ type: 'lock', queueId, lockId: lock.lockId })}>
                  <td><code>{lock.lockId}</code></td>
                  <td className={ui.num}>{formatCount(lock.sequence)}</td>
                  <td className={ui.num}>{lock.priority}</td>
                  <td className={ui.num}>{lock.deliveryCount} / {maxDeliveryCount}</td>
                  <td><Countdown lock={lock} now={now} /></td>
                  <td>{lock.competingConsumerMode ? 'competing' : 'exclusive'}</td>
                  <td className={ui.preview}>{lock.isBlobReference ? '📦 blob reference' : lock.text}</td>
                </tr>
              ))}
              {items.length === 0 && <tr><td colSpan={7} className={ui.muted}>No locked messages.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      {locks.hasNextPage && (
        <button className={ui.button} onClick={() => locks.fetchNextPage()} disabled={locks.isFetchingNextPage}>Load more ▸</button>
      )}
      {open && <MessageDrawer item={open} maxDeliveryCount={maxDeliveryCount} onClose={() => setOpen(null)} />}
    </>
  )
}

function Countdown({ lock, now }: { lock: Lock; now: number }) {
  const total = Math.max(1, lock.expiresAt - lock.createdAt)
  const left = Math.max(0, lock.expiresAt - now / 1000)
  const low = left < Math.min(5, total / 4)
  return (
    <span className={`${ui.countdown} ${low ? ui.countdownLow : ''}`}>
      <span className={ui.countdownTrack}><span className={ui.countdownFill} style={{ display: 'block', width: `${(left / total) * 100}%` }} /></span>
      {formatTimeLeft(lock.expiresAt, now)}{low && left > 0 ? ' ⚠' : ''}
    </span>
  )
}

function useNow(intervalMs = 1000) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(timer)
  }, [intervalMs])
  return now
}
