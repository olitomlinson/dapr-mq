import { useMetrics } from '../../api/hooks'
import type { MetricSeries, MetricsRange } from '../../api/types'
import { ui } from '../../components/ui'
import { TimeSeriesChart } from '../../components/charts'

/**
 * The KEDA overlay: for each ScaledObject scaling on this queue, desired vs actual consumer
 * replicas (or active Jobs for a ScaledJob). Without kube-state-metrics only an estimated target,
 * ceil(metric / target value), is shown. Renders nothing when KEDA isn't scaling on the queue.
 */
export function ScalingCharts({ queueId, range }: { queueId: string; range: MetricsRange }) {
  const scaling = useMetrics('scaling', { id: queueId, range })
  const series = scaling.data?.series ?? []
  const bindings = series.filter((x) => x.name === 'binding')
  if (bindings.length === 0) return null

  return (
    <>
      {bindings.map((binding) => (
        <ScaledObjectChart key={binding.labels.scaled_object} binding={binding} series={series.filter((x) => x.labels.scaled_object === binding.labels.scaled_object)} />
      ))}
    </>
  )
}

function ScaledObjectChart({ binding, series }: { binding: MetricSeries; series: MetricSeries[] }) {
  const named = (name: string) => series.find((x) => x.name === name)?.points ?? []
  const last = (name: string) => {
    const points = named(name)
    return points[points.length - 1]?.[1]
  }
  const target = Number(binding.labels.target_value)
  const { scaled_object: name, scaled_object_namespace: namespace, mode } = binding.labels
  const desired = named('desired')
  const jobs = named('jobs')
  const estimated: [number, number][] = named('metric').map(([t, v]) => [t, Math.ceil(v / target)])

  const lines = desired.length > 0
    ? [{ key: 'desired', label: 'Desired (KEDA/HPA)', points: desired }, { key: 'actual', label: 'Actual replicas', points: named('actual') }]
    : jobs.length > 0
      ? [{ key: 'jobs', label: 'Active jobs', points: jobs }]
      : [{ key: 'estimated', label: 'Estimated target', points: estimated }]
  const bounds = last('min') !== undefined ? `min ${last('min')} · max ${last('max')}` : null

  return (
    <div className={ui.card}>
      <p className={`${ui.muted} ${ui.small}`} style={{ marginTop: 0 }}>
        {jobs.length > 0 && desired.length === 0 ? 'ScaledJob' : 'ScaledObject'} <strong>{name}</strong> (ns: {namespace}) · target {target} {mode === 'sessions' ? 'sessions' : 'messages'}/instance
        {bounds && ` · ${bounds}`}
      </p>
      <TimeSeriesChart title="Consumers" lines={lines} />
      {desired.length === 0 && jobs.length === 0 && (
        <p className={`${ui.muted} ${ui.small}`}>Install kube-state-metrics to see desired and actual replicas; this is ceil(metric ÷ target).</p>
      )}
    </div>
  )
}
