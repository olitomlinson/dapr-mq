import { screen, within, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import type { MetricSeries } from '../api/types'
import { server } from './server'
import { api } from './handlers'
import { renderAt } from './render'

const series = (name: string, points: [number, number][], labels: Record<string, string> = {}): MetricSeries => ({ name, labels, points })

function metrics(byName: Record<string, MetricSeries[]>, seen: string[] = []) {
  server.use(http.get(api('/metrics/:name'), ({ params, request }) => {
    seen.push(`${params.name}${new URL(request.url).search}`)
    return HttpResponse.json({ series: byName[params.name as string] ?? [] })
  }))
  return seen
}

describe('Charts', () => {
  it('says metrics are not configured without Prometheus, and hides depth tiles', async () => {
    renderAt('/')
    expect(await screen.findByText(/Metrics not configured/)).toBeInTheDocument()
    expect(screen.queryByText('Ready')).not.toBeInTheDocument()
  })

  it('shows overview depth totals, throughput and the queues worth looking at', async () => {
    metrics({
      'depth-summary': [
        series('total', [[100, 12480]], { state: 'ready' }),
        series('total', [[100, 311]], { state: 'locked' }),
        series('top', [[100, 8210]], { queue: 'orders' }),
        series('dlq', [[100, 19]], { queue: 'orders' }),
      ],
      throughput: [series('enqueued', [[1, 100], [2, 210]]), series('dequeued', [[1, 90], [2, 180]]), series('deadlettered', [[1, 0], [2, 0.5]])],
    })
    renderAt('/')
    expect(await screen.findByRole('link', { name: /DLQs with messages ⚠\s*1/ })).toBeInTheDocument()
    expect(screen.getByText('12,480')).toBeInTheDocument()
    expect(screen.getByText('311')).toBeInTheDocument()
    const legend = screen.getByRole('heading', { name: 'Throughput' }).parentElement!
    expect(within(legend).getByText('210 msg/s')).toBeInTheDocument()
    expect(within(legend).getByText('0.5 msg/s')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'orders' })).toHaveAttribute('href', '/queues/orders')
    expect(screen.getByRole('link', { name: 'orders-deadletter' })).toHaveAttribute('href', '/queues/orders?tab=deadletter')
  })

  it('shows Prometheus in the sidebar only when it is configured', async () => {
    metrics({ status: [series('up', [[1, 1]])] })
    renderAt('/')
    expect(await screen.findByLabelText('Prometheus reachable')).toBeInTheDocument()
  })

  it('adds rates and a depth sparkline to the queue list for the rows on the page', async () => {
    const seen = metrics({
      'queue-list': [series('in', [[1, 210]], { queue: 'orders' }), series('out', [[1, 180]], { queue: 'orders' }), series('depth', [[1, 1], [2, 2]], { queue: 'orders' })],
    })
    renderAt('/queues')
    const row = (await screen.findByText('210')).closest('tr')!
    expect(within(row).getByText('180')).toBeInTheDocument()
    expect(within(row).getByRole('img', { name: 'orders depth over the last hour' })).toBeInTheDocument()
    expect(seen).toContain('queue-list?ids=orders%2Cemails%2Cnews-sub-audit')
  })

  it('charts a queue and re-queries when the range changes', async () => {
    const seen = metrics({
      queue: [series('enqueued', [[1, 5]]), series('dequeued', [[1, 4]]), series('ready', [[1, 8210]])],
    })
    renderAt('/queues/orders?tab=metrics')
    const produced = (await screen.findByRole('heading', { name: 'Produced vs consumed' })).parentElement!
    expect(await within(produced).findByText('5 msg/s')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: '6h' }))
    await waitFor(() => expect(seen).toContain('queue?id=orders&range=6h'))
  })

  it('sums dispatch across subscribers for the topic chart and shows each rate in the table', async () => {
    metrics({
      topic: [
        series('published', [[1, 40]]),
        series('dispatched', [[1, 40]], { subscriber: 'audit' }),
        series('dispatched', [[1, 31]], { subscriber: 'search' }),
      ],
    })
    renderAt('/topics/news')
    const chart = (await screen.findByRole('heading', { name: 'Publish vs dispatch' })).parentElement!
    expect(within(chart).getByText('71 msg/s')).toBeInTheDocument()
    expect(within(screen.getByText('search').closest('tr')!).getByText('31')).toBeInTheDocument()
  })
})
