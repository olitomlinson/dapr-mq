import type { ScalingTarget } from '../api/types'
import { health } from '../util/scaling'
import { ui } from './ui'

const tones = { ok: ui.badgeOk, warn: ui.badgeWarn, danger: ui.badgeDanger }

/** A ScaledObject's state from its Kubernetes conditions; nothing without Kubernetes read. */
export function HealthBadge({ target }: { target: ScalingTarget }) {
  const h = health(target)
  if (!h) return null
  return <span className={`${ui.badge} ${tones[h.tone]}`} title={h.title}>{h.label}</span>
}
