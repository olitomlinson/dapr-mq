import { useState } from 'react'
import { useMetrics } from '../../api/hooks'
import type { MetricsRange } from '../../api/types'
import { ui } from '../../components/ui'
import { MetricsState, RangePicker, TimeSeriesChart, seriesPoints } from '../../components/charts'

export function MetricsTab({ queueId }: { queueId: string }) {
  const [range, setRange] = useState<MetricsRange>('1h')
  const metrics = useMetrics('queue', { id: queueId, range })
  const p = (name: string) => seriesPoints(metrics.data?.series, name)

  return (
    <>
      <div className={ui.toolbar}>
        <span style={{ flex: 1 }} />
        <RangePicker value={range} onChange={setRange} />
      </div>
      <MetricsState error={metrics.error}>
        <div className={ui.grid2}>
          <div className={ui.card}>
            <TimeSeriesChart title="Produced vs consumed" unit="msg/s" lines={[
              { key: 'enqueued', label: 'Enqueued', points: p('enqueued') },
              { key: 'dequeued', label: 'Dequeued', points: p('dequeued') },
            ]} />
          </div>
          <div className={ui.card}>
            <TimeSeriesChart title="Settlement" unit="msg/s" lines={[
              { key: 'acknowledged', label: 'Ack', points: p('acknowledged') },
              { key: 'nacked', label: 'Nack', points: p('nacked') },
              { key: 'lock_expired', label: 'Expired', points: p('lock_expired') },
              { key: 'deadlettered', label: 'Dead-lettered', points: p('deadlettered') },
            ]} />
          </div>
          <div className={ui.card}>
            <TimeSeriesChart title="Depth" lines={[
              { key: 'ready', label: 'Ready', points: p('ready') },
              { key: 'locked', label: 'Locked', points: p('locked') },
              { key: 'dlq', label: 'Dead-letter', points: p('dlq') },
            ]} />
          </div>
        </div>
      </MetricsState>
    </>
  )
}
