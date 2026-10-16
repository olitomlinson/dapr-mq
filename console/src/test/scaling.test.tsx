import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from './server'
import { api, scaling, scalingWithKubernetes } from './handlers'
import { renderAt } from './render'

const withKubernetes = () => server.use(http.get(api('/scaling'), ({ request }) => {
  const queueId = new URL(request.url).searchParams.get('queueId')
  return HttpResponse.json({ ...scalingWithKubernetes, targets: scalingWithKubernetes.targets.filter((t) => !queueId || t.queueId === queueId) })
}))

describe('Scaling page', () => {
  it('lists each ScaledObject with its queue, metric and the replicas its metric asks for', async () => {
    renderAt('/scaling')
    const orders = (await screen.findByRole('link', { name: 'orders' })).closest('tr')!
    expect(orders).toHaveTextContent('apps/orders-consumer')
    expect(within(orders).getByRole('link', { name: 'orders' })).toHaveAttribute('href', '/queues/orders?tab=scaling')
    expect(within(orders).getByText('83')).toBeInTheDocument()
    expect(within(orders).getByText('9')).toBeInTheDocument()
    expect(within(orders).getByText('10 / replica')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'news-sub-search' })).toBeInTheDocument()
  })

  it('explains that replica counts need Kubernetes read access', async () => {
    renderAt('/scaling')
    expect(await screen.findByText(/console\.kubernetesRead/)).toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Replicas' })).not.toBeInTheDocument()
  })

  it('shows current and desired replicas, bounds and health with Kubernetes read access', async () => {
    withKubernetes()
    renderAt('/scaling')
    const orders = (await screen.findByRole('link', { name: 'orders' })).closest('tr')!
    expect(within(orders).getByText('6 → 8')).toBeInTheDocument()
    expect(within(orders).getByText('0–8')).toBeInTheDocument()
    expect(within(orders).getByText(/Ready/)).toBeInTheDocument()
    const search = screen.getByRole('link', { name: 'news-sub-search' }).closest('tr')!
    expect(within(search).getByText(/Not ready/)).toHaveAttribute('title', 'ScaledObjectCheckFailed: bad trigger')
    expect(screen.queryByText(/console\.kubernetesRead/)).not.toBeInTheDocument()
  })

  it('says how ScaledObjects appear when there are none', async () => {
    server.use(http.get(api('/scaling'), () => HttpResponse.json({ ...scaling, targets: [] })))
    renderAt('/scaling')
    expect(await screen.findByText(/No ScaledObjects use the DaprMQ scaler/)).toBeInTheDocument()
  })
})

describe('Overview scaling tile', () => {
  it('counts autoscaled queues and links to the scaling page', async () => {
    renderAt('/')
    expect(await screen.findByRole('link', { name: /KEDA-scaled\s*2/ })).toHaveAttribute('href', '/scaling')
  })

  it('is hidden when nothing is scaled by KEDA', async () => {
    server.use(http.get(api('/scaling'), () => HttpResponse.json({ ...scaling, targets: [] })))
    renderAt('/')
    await screen.findByRole('link', { name: /^Queues\s*42$/ })
    expect(screen.queryByText('KEDA-scaled')).not.toBeInTheDocument()
  })
})

describe('Queue scaling tab', () => {
  it('appears only on a scaled queue, with replica and metric charts', async () => {
    renderAt('/queues/orders')
    await userEvent.click(await screen.findByRole('tab', { name: 'Scaling' }))
    expect(screen.getByText(/apps\/orders-consumer/)).toBeInTheDocument()
    // Without Kubernetes read, the one series is what the metric asks for; the title names it.
    const replicas = screen.getByRole('heading', { name: 'Replicas asked for by metric' }).parentElement!
    expect(within(replicas).getByText('9')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Messages (ready + locked)' })).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Replicas asked for by metric chart' })).toBeInTheDocument()
  })

  it('charts current and desired replicas with Kubernetes read access', async () => {
    withKubernetes()
    renderAt('/queues/orders?tab=scaling')
    const replicas = (await screen.findByRole('heading', { name: 'Replicas' })).parentElement!
    expect(within(replicas).getByText('Current')).toBeInTheDocument()
    expect(within(replicas).getByText('Asked for by metric')).toBeInTheDocument()
    expect(within(replicas).getByText('Desired (HPA)')).toBeInTheDocument()
    expect(within(replicas).getByText('6')).toBeInTheDocument()
    expect(screen.getByText(/min 0 · max 8/)).toBeInTheDocument()
  })

  it('is absent on a queue KEDA does not scale', async () => {
    server.use(http.get(api('/scaling'), () => HttpResponse.json({ ...scaling, targets: [] })))
    renderAt('/queues/orders')
    await screen.findByRole('tab', { name: 'Overview' })
    expect(screen.queryByRole('tab', { name: 'Scaling' })).not.toBeInTheDocument()
  })
})

describe('Topic subscriber consumers', () => {
  it('shows the replicas a scaled subscription asks for, linking to its scaling tab', async () => {
    renderAt('/topics/news')
    const search = (await screen.findByText('search')).closest('tr')!
    expect(await within(search).findByRole('link', { name: '3 replicas' })).toHaveAttribute('href', '/queues/news-sub-search?tab=scaling')
    const audit = screen.getByText('audit').closest('tr')!
    expect(within(audit).getByText('–')).toBeInTheDocument()
  })
})
