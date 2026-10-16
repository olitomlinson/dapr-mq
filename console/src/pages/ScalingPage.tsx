import { Link } from 'react-router-dom'
import { useScaling } from '../api/hooks'
import { seg } from '../api/client'
import type { ScalingTarget } from '../api/types'
import { formatCount } from '../util/format'
import { isScaledJob, latestSample } from '../util/scaling'
import { QueryState, ui } from '../components/ui'
import { Sparkline } from '../components/charts'
import { HealthBadge } from '../components/HealthBadge'

export function ScalingPage() {
  const scaling = useScaling()
  const data = scaling.data
  const k8s = data?.kubernetesRead ?? false

  return (
    <div className={ui.page}>
      <h1 className={ui.title}>Scaling</h1>
      <QueryState isLoading={scaling.isLoading} error={scaling.error} />
      {data && !k8s && (
        <div className={`${ui.banner} ${ui.bannerInfo}`}>
          Showing what each queue's metric asks for, ceil(metric ÷ target). For running and desired replicas, bounds and
          health, give the console read access to ScaledObjects and HPAs with the Helm value console.kubernetesRead.
        </div>
      )}
      {data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead>
              <tr>
                <th>ScaledObject</th>
                <th>Queue</th>
                <th className={ui.num}>Target</th>
                <th className={ui.num}>Metric</th>
                <th className={ui.num}>Asked for</th>
                {k8s && <th className={ui.num}>Replicas</th>}
                {k8s && <th className={ui.num}>Min–max</th>}
                {k8s && <th>Health</th>}
                <th>Last hour</th>
              </tr>
            </thead>
            <tbody>
              {data.targets.map((t) => <Row key={`${t.namespace}/${t.name}/${t.mode}/${t.queueId}`} target={t} k8s={k8s} />)}
              {data.targets.length === 0 && (
                <tr>
                  <td colSpan={k8s ? 9 : 6} className={ui.muted}>
                    No ScaledObjects use the DaprMQ scaler. They appear here within a minute of KEDA first calling it.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
      {data && (
        <p className={`${ui.muted} ${ui.small}`}>
          Sampled every {data.sampleIntervalSeconds}s by this console pod and kept in memory for {Math.round(data.historySeconds / 60)} minutes,
          so history restarts with the pod.
        </p>
      )}
    </div>
  )
}

function Row({ target: t, k8s }: { target: ScalingTarget; k8s: boolean }) {
  const last = latestSample(t)
  const k = t.kubernetes
  const value = (n?: number | null) => (n == null ? '–' : formatCount(n))
  const replicas = isScaledJob(t)
    ? `${value(k?.activeJobs)} jobs`
    : `${value(k?.currentReplicas)} → ${value(k?.desiredReplicas)}`
  const trend: [number, number | null][] = t.samples.map((s) =>
    [s.t, (k8s ? (isScaledJob(t) ? s.activeJobs : s.currentReplicas) : s.desiredByMetric) ?? null])

  return (
    <tr>
      <td>{t.namespace}/{t.name}</td>
      <td><Link to={`/queues/${seg(t.queueId)}?tab=scaling`}>{t.queueId}</Link></td>
      <td className={ui.num}>{formatCount(t.targetValue)} / replica</td>
      <td className={ui.num}>{value(last?.metric)}</td>
      <td className={ui.num}>{value(last?.desiredByMetric)}</td>
      {k8s && <td className={ui.num}>{k && !k.error ? replicas : '–'}</td>}
      {k8s && <td className={ui.num}>{k?.minReplicas != null ? `${k.minReplicas}–${k.maxReplicas}` : '–'}</td>}
      {k8s && <td><HealthBadge target={t} /></td>}
      <td><Sparkline points={trend} label={`${t.name} ${k8s ? 'replicas' : 'replicas asked for'}, last hour`} /></td>
    </tr>
  )
}
