import type { ScalingSample, ScalingTarget } from '../api/types'

export const latestSample = (target: ScalingTarget): ScalingSample | undefined => target.samples[target.samples.length - 1]

export const isScaledJob = (target: ScalingTarget) => target.kubernetes?.kind === 'ScaledJob'

/** What the trigger's metric counts. */
export function metricLabel(target: ScalingTarget): string {
  if (target.mode === 'Sessions') return 'Non-empty sessions'
  return target.includeDeadLetter ? 'Messages (ready + locked, incl. DLQ)' : 'Messages (ready + locked)'
}

/** The consumer count to show for a target: running replicas (or jobs) if known, else what the metric asks for. */
export function replicasNow(target: ScalingTarget): number | null {
  const k = target.kubernetes
  if (k && !k.error) return (isScaledJob(target) ? k.activeJobs : k.currentReplicas) ?? null
  return latestSample(target)?.desiredByMetric ?? null
}

export type Health = { label: string; tone: 'ok' | 'warn' | 'danger'; title?: string }

/** The ScaledObject's state from its conditions (Kubernetes read only); null without it. */
export function health(target: ScalingTarget): Health | null {
  const k = target.kubernetes
  if (!k) return null
  if (k.error) return { label: 'Unreadable', tone: 'warn', title: k.error }
  const condition = (type: string) => k.conditions.find((c) => c.type === type)
  const describe = (type: string) => {
    const c = condition(type)
    return c?.reason || c?.message ? [c.reason, c.message].filter(Boolean).join(': ') : undefined
  }
  if (condition('Fallback')?.status === 'True') return { label: 'Fallback', tone: 'danger', title: describe('Fallback') }
  if (condition('Ready')?.status === 'False') return { label: 'Not ready', tone: 'danger', title: describe('Ready') }
  if (condition('Paused')?.status === 'True') return { label: 'Paused', tone: 'warn', title: describe('Paused') }
  if (condition('Ready')?.status === 'True') return { label: '● Ready', tone: 'ok' }
  return { label: 'Unknown', tone: 'warn' }
}
