// Shapes returned by the operator's /api/console/* (camelCase JSON of the C# models in
// DaprMQ.Interfaces/AdminModels.cs, CatalogModels.cs and DaprMQ.Operator/Console/ConsoleModels.cs).
// Timestamps are unix seconds.

export type CatalogEntryKind = 'Queue' | 'DeadLetter' | 'Topic' | 'Sink'

export interface CatalogEntry {
  id: string
  kind: CatalogEntryKind
  parentId?: string | null
  registeredAt: string
}

export interface CatalogPage {
  entries: CatalogEntry[]
  nextCursor?: string | null
  totals: Partial<Record<CatalogEntryKind, number>>
}

export interface QueueRow {
  id: string
  kind: CatalogEntryKind
  parentId?: string | null
  ready: number
  locked: number
  deadLetterCount?: number | null
  error?: string | null
}

export interface QueuePage {
  items: QueueRow[]
  nextCursor?: string | null
  totals: Partial<Record<CatalogEntryKind, number>>
}

/** A queue's depth on the overview; on a DLQ row, id is the queue the DLQ belongs to. */
export interface QueueDepth {
  id: string
  ready: number
  locked: number
}

/** /api/console/overview: every catalogued queue's depth from committed state, summed and ranked. */
export interface Overview {
  totals: Partial<Record<CatalogEntryKind, number>>
  ready: number
  locked: number
  deadLettered: number
  deadLetterQueuesWithMessages: number
  topQueues: QueueDepth[]
  deadLetterQueues: QueueDepth[]
  queuesRead: number
  truncated: boolean
  errors: number
}

export interface PriorityDetail {
  priority: number
  count: number
  headSegment: number
  tailSegment: number
  headOffloadedSegment?: number | null
  tailOffloadedSegment?: number | null
}

export interface SinkDetail {
  url: string
  maxConcurrency: number
  lockTtlSeconds: number
  currentIntervalSeconds: number
}

export interface QueueDetail {
  queueId: string
  priorities: PriorityDetail[]
  ready: number
  locked: number
  nextSequence: number
  segmentSize: number
  dedupEnabled: boolean
  errorMessage?: string | null
  sessionLeaseExpiresAt?: number | null
  sessionCount: number
  deadLetterReady: number
  deadLetterLocked: number
  maxDeliveryCount: number
  sink?: SinkDetail | null
}

interface ItemText {
  sizeBytes: number
  text: string
  textTruncated: boolean
  isBlobReference: boolean
  blobSize?: number | null
  blobContentType?: string | null
}

export interface Message extends ItemText {
  location: string
  priority: number
  sequence: number
  deliveryCount: number
}

export interface MessagePage {
  items: Message[]
  nextCursor?: string | null
}

export interface Lock extends ItemText {
  lockId: string
  createdAt: number
  expiresAt: number
  priority: number
  sequence: number
  deliveryCount: number
  competingConsumerMode: boolean
}

export interface LockPage {
  items: Lock[]
  nextCursor?: string | null
}

export interface Session {
  sessionId: string
  ready: number
  locked: number
  leaseExpiresAt?: number | null
}

export interface SessionPage {
  items: Session[]
  nextCursor?: string | null
}

export interface CircuitBreaker {
  consecutiveFailures: number
  firstFailureAt?: number | null
  nextRetryAt?: number | null
  blacklisted: boolean
}

export interface TopicSubscriber {
  subscriberId: string
  queueId: string
  dispatchCursor: number
  lag: number
  circuitBreaker?: CircuitBreaker | null
}

export interface TopicDetail {
  topicId: string
  subscribers: TopicSubscriber[]
  nextSequence: number
  oldestSequence: number
  currentGeneration: number
  itemLogCount: number
  pendingPublishCount: number
}

export interface Payload {
  contentType?: string | null
  sizeBytes: number
  contentBase64: string
  truncated: boolean
}


/** One sample of a KEDA trigger; replica fields need the console's Kubernetes read access. */
export interface ScalingSample {
  t: number
  /** What the scaler reports; null when the depth read failed. */
  metric?: number | null
  /** ceil(metric ÷ target), or 0 at or below activation - before min/max and HPA smoothing. */
  desiredByMetric?: number | null
  currentReplicas?: number | null
  desiredReplicas?: number | null
  activeJobs?: number | null
}

export interface ScalingCondition {
  type: string
  status: string
  reason?: string | null
  message?: string | null
}

export interface KubernetesScalingStatus {
  kind?: 'ScaledObject' | 'ScaledJob' | null
  minReplicas?: number | null
  maxReplicas?: number | null
  currentReplicas?: number | null
  desiredReplicas?: number | null
  activeJobs?: number | null
  conditions: ScalingCondition[]
  error?: string | null
}

/** A KEDA trigger scaling on a DaprMQ queue, as the operator's scaler last saw it. */
export interface ScalingTarget {
  namespace: string
  name: string
  queueId: string
  mode: 'Messages' | 'Sessions'
  targetValue: number
  activationValue: number
  includeDeadLetter: boolean
  lastSeenAt: number
  kubernetes?: KubernetesScalingStatus | null
  samples: ScalingSample[]
}

/** /api/console/scaling: the console's in-memory KEDA history. */
export interface Scaling {
  kubernetesRead: boolean
  sampleIntervalSeconds: number
  historySeconds: number
  targets: ScalingTarget[]
}
