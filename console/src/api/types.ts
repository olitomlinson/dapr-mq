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
