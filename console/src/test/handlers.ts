import { http, HttpResponse } from 'msw'
import type {
  CatalogPage, Lock, LockPage, Message, MessagePage, Overview, QueueDetail, QueuePage, Scaling, ScalingTarget, SessionPage, TopicDetail,
} from '../api/types'

// Fixtures shaped like /api/console/* replies. Tests override per case with server.use(...).

export const api = (path: string) => `*/api/console${path}`

export const catalog: CatalogPage = {
  entries: [],
  nextCursor: null,
  totals: { Queue: 42, DeadLetter: 3, Topic: 6, Sink: 2 },
}

export const overview: Overview = {
  totals: { Queue: 42, DeadLetter: 3, Topic: 6, Sink: 2 },
  ready: 12480,
  locked: 311,
  deadLettered: 27,
  deadLetterQueuesWithMessages: 2,
  topQueues: [{ id: 'orders', ready: 8210, locked: 120 }, { id: 'emails', ready: 2004, locked: 40 }],
  deadLetterQueues: [{ id: 'orders', ready: 19, locked: 0 }, { id: 'payments', ready: 8, locked: 0 }],
  queuesRead: 42,
  truncated: false,
  errors: 0,
}

export const queuePage: QueuePage = {
  items: [
    { id: 'orders', kind: 'Queue', ready: 8210, locked: 120, deadLetterCount: 19 },
    { id: 'emails', kind: 'Queue', ready: 0, locked: 0, deadLetterCount: 0 },
    { id: 'news-sub-audit', kind: 'Queue', ready: 12, locked: 0, deadLetterCount: 0 },
  ],
  nextCursor: 'news-sub-audit',
  totals: { Queue: 142 },
}

export const ordersDetail: QueueDetail = {
  queueId: 'orders',
  priorities: [
    { priority: 0, count: 1210, headSegment: 0, tailSegment: 12 },
    { priority: 1, count: 7000, headSegment: 3, tailSegment: 73 },
  ],
  ready: 8210,
  locked: 120,
  nextSequence: 1048221,
  segmentSize: 100,
  dedupEnabled: true,
  sessionCount: 0,
  deadLetterReady: 19,
  deadLetterLocked: 0,
  maxDeliveryCount: 10,
  sink: { url: 'https://svc/orders', maxConcurrency: 8, lockTtlSeconds: 30, currentIntervalSeconds: 2 },
}

const message = (sequence: number, text: string, extra: Partial<Message> = {}): Message => ({
  location: `1:3:${sequence % 100}`,
  priority: 1,
  sequence,
  deliveryCount: 0,
  sizeBytes: text.length,
  text,
  textTruncated: false,
  isBlobReference: false,
  ...extra,
})

export const messagePage: MessagePage = {
  items: [
    message(1040011, '{"orderId":"A-991","total":42.1}'),
    message(1040015, '{"orderId":"A-993","total":8.0}', { deliveryCount: 2 }),
    message(1040016, '{"__daprmq_blob_ref__":"ref-1","size":4404019}', { isBlobReference: true, blobSize: 4404019 }),
  ],
  nextCursor: '1:3:1040016',
}

export const lock: Lock = {
  lockId: '7f3aX9c21Qa',
  createdAt: 1_700_000_000,
  expiresAt: 1_700_000_030,
  priority: 1,
  sequence: 1039990,
  deliveryCount: 1,
  competingConsumerMode: true,
  sizeBytes: 20,
  text: '{"orderId":"A-990"}',
  textTruncated: false,
  isBlobReference: false,
}

export const topic: TopicDetail = {
  topicId: 'news',
  subscribers: [
    { subscriberId: 'audit', queueId: 'news-sub-audit', dispatchCursor: 1204, lag: 0 },
    { subscriberId: 'search', queueId: 'news-sub-search', dispatchCursor: 1084, lag: 120, circuitBreaker: { consecutiveFailures: 5, blacklisted: false } },
  ],
  nextSequence: 1204,
  oldestSequence: 1000,
  currentGeneration: 4,
  itemLogCount: 204,
  pendingPublishCount: 3,
}

export const ordersScaling: ScalingTarget = {
  namespace: 'apps',
  name: 'orders-consumer',
  queueId: 'orders',
  mode: 'Messages',
  targetValue: 10,
  activationValue: 0,
  includeDeadLetter: false,
  lastSeenAt: 1_700_000_100,
  kubernetes: null,
  samples: [
    { t: 1_700_000_000, metric: 40, desiredByMetric: 4 },
    { t: 1_700_000_015, metric: 83, desiredByMetric: 9 },
  ],
}

export const searchScaling: ScalingTarget = {
  ...ordersScaling,
  name: 'search-indexer',
  queueId: 'news-sub-search',
  targetValue: 50,
  samples: [{ t: 1_700_000_015, metric: 120, desiredByMetric: 3 }],
}

export const scaling: Scaling = {
  kubernetesRead: false,
  sampleIntervalSeconds: 15,
  historySeconds: 3600,
  targets: [ordersScaling, searchScaling],
}

/** The same targets, read with Kubernetes access: replica counts, bounds and conditions. */
export const scalingWithKubernetes: Scaling = {
  ...scaling,
  kubernetesRead: true,
  targets: [
    {
      ...ordersScaling,
      kubernetes: { kind: 'ScaledObject', minReplicas: 0, maxReplicas: 8, currentReplicas: 6, desiredReplicas: 8, conditions: [{ type: 'Ready', status: 'True' }, { type: 'Fallback', status: 'False' }] },
      samples: ordersScaling.samples.map((sample, i) => ({ ...sample, currentReplicas: [4, 6][i], desiredReplicas: [4, 8][i] })),
    },
    {
      ...searchScaling,
      kubernetes: { kind: 'ScaledObject', minReplicas: 1, maxReplicas: 5, currentReplicas: 3, desiredReplicas: 3, conditions: [{ type: 'Ready', status: 'False', reason: 'ScaledObjectCheckFailed', message: 'bad trigger' }] },
    },
  ],
}

export const handlers = [
  http.get(api('/scaling'), ({ request }) => {
    const queueId = new URL(request.url).searchParams.get('queueId')
    return HttpResponse.json({ ...scaling, targets: scaling.targets.filter((t) => !queueId || t.queueId === queueId) })
  }),
  http.get(api('/catalog'), () => HttpResponse.json(catalog)),
  http.get(api('/queues'), () => HttpResponse.json(queuePage)),
  http.get(api('/queues/orders'), () => HttpResponse.json(ordersDetail)),
  http.get(api('/queues/:id/messages'), () => HttpResponse.json(messagePage)),
  http.get(api('/queues/:id/messages/:location'), ({ request }) => {
    const sequence = Number(new URL(request.url).searchParams.get('sequence'))
    const found = messagePage.items.find((m) => m.sequence === sequence)
    return found ? HttpResponse.json(found) : new HttpResponse(null, { status: 410 })
  }),
  http.get(api('/queues/:id/locks'), () => HttpResponse.json({ items: [lock], nextCursor: null } satisfies LockPage)),
  http.get(api('/queues/:id/locks/:lockId'), () => HttpResponse.json(lock)),
  http.get(api('/queues/:id/sessions'), () =>
    HttpResponse.json({ items: [{ sessionId: 'a', ready: 2, locked: 1, leaseExpiresAt: null }], nextCursor: null } satisfies SessionPage)),
  http.get(api('/topics/news'), () => HttpResponse.json(topic)),
  http.get(api('/overview'), () => HttpResponse.json(overview)),
]
