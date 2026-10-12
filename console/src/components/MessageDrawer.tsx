import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'
import { useItem, usePayload, type ItemRef } from '../api/hooks'
import type { Lock, Message } from '../api/types'
import { base64ToBytes, formatBytes, formatCount, formatTimeLeft, hexDump, prettyJson, utf8 } from '../util/format'
import s from './MessageDrawer.module.css'
import { ui } from './ui'

type View = 'json' | 'raw' | 'hex'

/** One message or lock, fetched in full (≤ 1 MB). A blob reference's payload loads only on request. */
export function MessageDrawer({ item, maxDeliveryCount, onClose }: { item: ItemRef; maxDeliveryCount: number; onClose: () => void }) {
  const full = useItem(item)
  const [loadPayload, setLoadPayload] = useState(false)
  const payload = usePayload(item, loadPayload)
  const [view, setView] = useState<View>('json')

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => event.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const data = full.data
  const gone = full.error instanceof ApiError && (full.error.status === 410 || full.error.status === 404)
  const bytes = payload.data ? base64ToBytes(payload.data.contentBase64) : data ? utf8(data.text) : null
  const text = payload.data ? new TextDecoder().decode(bytes!) : data?.text ?? ''
  const shown = view === 'hex' && bytes ? hexDump(bytes) : view === 'json' ? prettyJson(text) ?? text : text

  return (
    <>
      <div className={s.backdrop} onClick={onClose} />
      <aside className={s.drawer} role="dialog" aria-label="Message">
        <div className={s.head}>
          <h2>{data ? `Message seq ${formatCount(data.sequence)}` : 'Message'}</h2>
          <button className={ui.button} onClick={onClose} aria-label="Close">✕</button>
        </div>
        {full.isLoading && <p className={ui.muted}>Loading…</p>}
        {gone && <div className={`${ui.banner} ${ui.bannerInfo}`}>Consumed since listed.</div>}
        {full.error && !gone && <div className={`${ui.banner} ${ui.bannerDanger}`}>{(full.error as Error).message}</div>}
        {data && (
          <>
            <dl className={ui.kv}>
              <dt>Queue</dt><dd>{item.queueId}</dd>
              <dt>Priority</dt><dd>{data.priority}</dd>
              <dt>Deliveries</dt><dd>{data.deliveryCount} / {maxDeliveryCount}</dd>
              {'location' in data && (<><dt>Location</dt><dd>{(data as Message).location}</dd></>)}
              {'lockId' in data && <LockInfo lock={data as Lock} />}
              <dt>Size</dt><dd>{formatBytes(data.sizeBytes)}{data.textTruncated ? ' (showing the first 1 MB)' : ''}</dd>
            </dl>
            {data.isBlobReference && !payload.data && (
              <div>
                <button className={ui.button} onClick={() => setLoadPayload(true)} disabled={payload.isFetching}>
                  {payload.isFetching ? 'Loading…' : `Load payload${data.blobSize != null ? ` (${formatBytes(data.blobSize)})` : ''}`}
                </button>
                {payload.error && <span className={ui.muted}> {(payload.error as Error).message}</span>}
              </div>
            )}
            {payload.data?.truncated && <p className={`${ui.muted} ${ui.small}`}>Showing the first 1 MB of {formatBytes(payload.data.sizeBytes)}.</p>}
            <div className={s.views}>
              {(['json', 'raw', 'hex'] as View[]).map((v) => (
                <button key={v} className={s.view} aria-pressed={view === v} onClick={() => setView(v)}>
                  {v === 'json' ? 'JSON' : v === 'raw' ? 'Raw' : 'Hex'}
                </button>
              ))}
              <span style={{ flex: 1 }} />
              <button className={ui.button} onClick={() => navigator.clipboard?.writeText(text)}>Copy</button>
            </div>
            <pre className={s.body}>{shown}</pre>
          </>
        )}
      </aside>
    </>
  )
}

function LockInfo({ lock }: { lock: Lock }) {
  return (
    <>
      <dt>Lock</dt><dd><code>{lock.lockId}</code> · {lock.competingConsumerMode ? 'competing' : 'exclusive'}</dd>
      <dt>Expires in</dt><dd>{formatTimeLeft(lock.expiresAt, Date.now())}</dd>
    </>
  )
}
