import type { ScalingTarget } from '../../api/types'
import { formatCount, formatTime } from '../../util/format'
import { isScaledJob, metricLabel } from '../../util/scaling'
import { ui } from '../../components/ui'
import { TimeSeriesChart, type ChartLine } from '../../components/charts'
import { HealthBadge } from '../../components/HealthBadge'

/** Each KEDA trigger scaling on this queue: replicas over time, and the metric KEDA scales on. */
export function ScalingTab({ targets, kubernetesRead }: { targets: ScalingTarget[]; kubernetesRead: boolean }) {
  return (
    <>
      {targets.map((t) => <TargetCard key={`${t.namespace}/${t.name}/${t.mode}`} target={t} kubernetesRead={kubernetesRead} />)}
    </>
  )
}

function TargetCard({ target: t, kubernetesRead }: { target: ScalingTarget; kubernetesRead: boolean }) {
  const k = t.kubernetes
  const series = (pick: (s: ScalingTarget['samples'][number]) => number | null | undefined): [number, number | null][] =>
    t.samples.map((s) => [s.t, pick(s) ?? null])
  const askedFor: ChartLine = { key: 'byMetric', label: 'Asked for by metric', points: series((s) => s.desiredByMetric) }
  const withReplicas = kubernetesRead && k && !k.error
  const lines: ChartLine[] = !withReplicas
    ? [askedFor]
    : isScaledJob(t)
      ? [{ key: 'jobs', label: 'Active jobs', points: series((s) => s.activeJobs) }, askedFor]
      : [
          { key: 'current', label: 'Current', points: series((s) => s.currentReplicas) },
          { key: 'desired', label: 'Desired (HPA)', points: series((s) => s.desiredReplicas) },
          askedFor,
        ]
  const unit = t.mode === 'Sessions' ? 'sessions' : 'messages'

  return (
    <div className={ui.card}>
      <p className={`${ui.muted} ${ui.small}`} style={{ marginTop: 0 }}>
        {k?.kind ?? 'ScaledObject'} <strong>{t.namespace}/{t.name}</strong> · target {formatCount(t.targetValue)} {unit}/replica
        {t.activationValue > 0 && ` · activation ${formatCount(t.activationValue)}`}
        {k?.minReplicas != null && ` · min ${k.minReplicas} · max ${k.maxReplicas}`}
        {` · KEDA last asked ${formatTime(t.lastSeenAt)}`}
        {' '}<HealthBadge target={t} />
      </p>
      <div className={ui.grid2}>
        <TimeSeriesChart title={withReplicas ? 'Replicas' : 'Replicas asked for by metric'} lines={lines} />
        <TimeSeriesChart title={metricLabel(t)} lines={[{ key: 'metric', label: metricLabel(t), points: series((s) => s.metric) }]} />
      </div>
      <p className={`${ui.muted} ${ui.small}`} style={{ marginBottom: 0 }}>
        Asked for by metric is ceil(metric ÷ target), or 0 at or below activation: before min/max, HPA tolerance and stabilization.
        {!kubernetesRead && ' Set the Helm value console.kubernetesRead to chart actual and desired replicas.'}
        {k?.error && ` Kubernetes read failed: ${k.error}`}
      </p>
    </div>
  )
}
