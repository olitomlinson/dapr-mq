import { useState } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { ApiError } from './api/client'
import { RefreshProvider } from './api/refresh'
import { Shell } from './components/Shell'
import { OverviewPage } from './pages/OverviewPage'
import { QueuesPage } from './pages/QueuesPage'
import { QueueDetailPage } from './pages/QueueDetailPage'
import { TopicsPage } from './pages/TopicsPage'
import { TopicDetailPage } from './pages/TopicDetailPage'
import { SinksPage } from './pages/SinksPage'
import { ScalingPage } from './pages/ScalingPage'

export function AppRoutes() {
  return (
    <Routes>
      <Route element={<Shell />}>
        <Route path="/" element={<OverviewPage />} />
        <Route path="/queues" element={<QueuesPage />} />
        <Route path="/queues/:queueId" element={<QueueDetailPage />} />
        <Route path="/topics" element={<TopicsPage />} />
        <Route path="/topics/:topicId" element={<TopicDetailPage />} />
        <Route path="/sinks" element={<SinksPage />} />
        <Route path="/scaling" element={<ScalingPage />} />
      </Route>
    </Routes>
  )
}

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // 4xx answers (404 not found, 410 gone) are final; only retry unavailability.
        retry: (failures, error) => !(error instanceof ApiError && error.status < 500) && failures < 2,
      },
    },
  })
}

export function App() {
  const [queryClient] = useState(createQueryClient)
  return (
    <QueryClientProvider client={queryClient}>
      <RefreshProvider>
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </RefreshProvider>
    </QueryClientProvider>
  )
}
