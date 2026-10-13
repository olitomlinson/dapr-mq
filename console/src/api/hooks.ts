import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { getJson, seg } from './client'
import { usePollInterval } from './refresh'
import type {
  CatalogEntryKind, CatalogPage, Lock, LockPage, Message, MessagePage, Overview, Payload,
  QueueDetail, QueuePage, SessionPage, TopicDetail,
} from './types'

// Browse queries are snapshots: fetched on open and on an explicit Refresh, never polled or
// refetched on focus, since each page loads whole segments on a worker.
const snapshot = { staleTime: Infinity, refetchOnWindowFocus: false, refetchOnReconnect: false } as const

export function useCatalog(kind: CatalogEntryKind | null, cursor: string | null, limit = 100) {
  const refetchInterval = usePollInterval()
  return useQuery({
    queryKey: ['catalog', kind, cursor, limit],
    queryFn: () => getJson<CatalogPage>('/catalog', { kind, cursor, limit }),
    refetchInterval,
  })
}

export function useOverview() {
  const refetchInterval = usePollInterval()
  return useQuery({
    queryKey: ['overview'],
    queryFn: () => getJson<Overview>('/overview'),
    refetchInterval,
    placeholderData: (previous) => previous,
  })
}

export function useQueuePage(kind: 'Queue' | 'DeadLetter', cursor: string | null) {
  const refetchInterval = usePollInterval()
  return useQuery({
    queryKey: ['queues', kind, cursor],
    queryFn: () => getJson<QueuePage>('/queues', { kind, cursor }),
    refetchInterval,
    placeholderData: (previous) => previous,
  })
}

export function useQueueDetail(queueId: string) {
  const refetchInterval = usePollInterval()
  return useQuery({
    queryKey: ['queue', queueId],
    queryFn: () => getJson<QueueDetail>(`/queues/${seg(queueId)}`),
    refetchInterval,
  })
}

export function useTopic(topicId: string) {
  const refetchInterval = usePollInterval()
  return useQuery({
    queryKey: ['topic', topicId],
    queryFn: () => getJson<TopicDetail>(`/topics/${seg(topicId)}`),
    refetchInterval,
  })
}

export function useMessages(queueId: string, priority: number | null) {
  return useInfiniteQuery({
    queryKey: ['messages', queueId, priority],
    queryFn: ({ pageParam }) =>
      getJson<MessagePage>(`/queues/${seg(queueId)}/messages`, { priority, cursor: pageParam, limit: 100 }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    ...snapshot,
  })
}

export function useLocks(queueId: string) {
  return useInfiniteQuery({
    queryKey: ['locks', queueId],
    queryFn: ({ pageParam }) => getJson<LockPage>(`/queues/${seg(queueId)}/locks`, { cursor: pageParam, limit: 100 }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    ...snapshot,
  })
}

export function useSessions(queueId: string) {
  return useInfiniteQuery({
    queryKey: ['sessions', queueId],
    queryFn: ({ pageParam }) => getJson<SessionPage>(`/queues/${seg(queueId)}/sessions`, { cursor: pageParam, limit: 100 }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    ...snapshot,
  })
}

/** What the message drawer shows: a ready message (by location + sequence) or a lock. */
export type ItemRef =
  | { type: 'message'; queueId: string; location: string; sequence: number }
  | { type: 'lock'; queueId: string; lockId: string }

function itemPath(ref: ItemRef) {
  return ref.type === 'message'
    ? `/queues/${seg(ref.queueId)}/messages/${seg(ref.location)}`
    : `/queues/${seg(ref.queueId)}/locks/${seg(ref.lockId)}`
}

function itemParams(ref: ItemRef) {
  return ref.type === 'message' ? { sequence: ref.sequence } : {}
}

export function useItem(ref: ItemRef) {
  return useQuery({
    queryKey: ['item', ref],
    queryFn: () => getJson<Message | Lock>(itemPath(ref), itemParams(ref)),
    retry: false,
    ...snapshot,
  })
}

export function usePayload(ref: ItemRef, enabled: boolean) {
  return useQuery({
    queryKey: ['payload', ref],
    queryFn: () => getJson<Payload>(`${itemPath(ref)}/payload`, itemParams(ref)),
    enabled,
    retry: false,
    ...snapshot,
  })
}

