import { screen, within, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from './server'
import { api, ordersDetail, overview, queuePage } from './handlers'
import { renderAt } from './render'

describe('Overview', () => {
  it('shows catalog totals and depth totals as tiles', async () => {
    renderAt('/')
    const queues = await screen.findByRole('link', { name: /^Queues\s*42$/ })
    expect(queues).toHaveAttribute('href', '/queues')
    expect(screen.getByRole('link', { name: /Topics\s*6/ })).toBeInTheDocument()
    expect(screen.getByText('12,480')).toBeInTheDocument()
    expect(screen.getByText('311')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /DLQs with messages ⚠\s*2/ })).toHaveAttribute('href', '/queues?kind=DeadLetter')
  })

  it('ranks the top queues and the DLQs with messages, linking to each', async () => {
    renderAt('/')
    const top = (await screen.findByRole('heading', { name: 'Top queues by depth' })).parentElement!
    expect(within(top).getByRole('link', { name: 'orders' })).toHaveAttribute('href', '/queues/orders')
    expect(within(top).getByText('8,330')).toBeInTheDocument()
    const dlqs = screen.getByRole('heading', { name: 'DLQs with messages' }).parentElement!
    expect(within(dlqs).getByRole('link', { name: 'payments-deadletter' })).toHaveAttribute('href', '/queues/payments?tab=deadletter')
  })

  it('counts every DLQ when none has messages', async () => {
    server.use(http.get(api('/overview'), () => HttpResponse.json({ ...overview, deadLetterQueuesWithMessages: 0, deadLetterQueues: [] })))
    renderAt('/')
    expect(await screen.findByRole('link', { name: /^Dead-letter queues\s*3$/ })).toBeInTheDocument()
    expect(screen.getByText('No dead-lettered messages.')).toBeInTheDocument()
  })

  it('says when the overview stopped short or some reads failed', async () => {
    server.use(http.get(api('/overview'), () => HttpResponse.json({ ...overview, truncated: true, queuesRead: 10000, errors: 3 })))
    renderAt('/')
    expect(await screen.findByText(/first 10,000 queues/)).toBeInTheDocument()
    expect(screen.getByText(/3 depth reads failed/)).toBeInTheDocument()
  })

  it('marks workers unreachable when the API is unavailable', async () => {
    server.use(
      http.get(api('/catalog'), () => new HttpResponse(null, { status: 503 })),
      http.get(api('/overview'), () => new HttpResponse(null, { status: 503 })),
    )
    renderAt('/')
    expect(await screen.findByText(/Workers unavailable/)).toBeInTheDocument()
    expect(screen.getAllByLabelText('unreachable').length).toBeGreaterThan(0)
  })
})

describe('Queues', () => {
  it('lists queues with depth, DLQ links and subscription links', async () => {
    renderAt('/queues')
    const orders = (await screen.findByRole('link', { name: 'orders' })).closest('tr')!
    expect(within(orders).getByText('8,210')).toBeInTheDocument()
    expect(within(orders).getByRole('link', { name: '19' })).toHaveAttribute('href', '/queues/orders?tab=deadletter')
    const sub = screen.getByRole('link', { name: 'news-sub-audit' }).closest('tr')!
    expect(within(sub).getByRole('link', { name: 'sub ↗' })).toHaveAttribute('href', '/topics/news')
    expect(screen.getByText('142')).toBeInTheDocument()
  })

  it('hides empty queues on request', async () => {
    renderAt('/queues')
    await screen.findByRole('link', { name: 'emails' })
    await userEvent.click(screen.getByLabelText(/Hide empty/))
    expect(screen.queryByRole('link', { name: 'emails' })).not.toBeInTheDocument()
  })

  it('pages with the cursor, and switches to dead-letter queues', async () => {
    const seen: string[] = []
    server.use(http.get(api('/queues'), ({ request }) => {
      seen.push(new URL(request.url).search)
      return HttpResponse.json(queuePage)
    }))
    renderAt('/queues')
    await userEvent.click(await screen.findByRole('button', { name: /Next/ }))
    await waitFor(() => expect(seen).toContain('?kind=Queue&cursor=news-sub-audit'))
    await userEvent.selectOptions(screen.getByLabelText('Kind'), 'DeadLetter')
    await waitFor(() => expect(seen).toContain('?kind=DeadLetter'))
  })
})

describe('Queue detail', () => {
  it('shows configuration, priorities and the sink', async () => {
    renderAt('/queues/orders')
    expect(await screen.findByText('1,048,221')).toBeInTheDocument()
    expect(screen.getByText('P0 (fast)')).toBeInTheDocument()
    expect(screen.getByText('https://svc/orders')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Messages 8,210' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Dead-letter 19' })).toBeInTheDocument()
  })

  it('shows the corrupt banner', async () => {
    server.use(http.get(api('/queues/orders'), () => HttpResponse.json({ ...ordersDetail, errorMessage: 'CORRUPTED: segment 4 missing' })))
    renderAt('/queues/orders')
    expect(await screen.findByRole('alert')).toHaveTextContent('CORRUPTED: segment 4 missing')
  })

  it('says so for an unknown queue', async () => {
    server.use(http.get(api('/queues/nope'), () => new HttpResponse(null, { status: 404 })))
    renderAt('/queues/nope')
    expect(await screen.findByText(/No such queue/)).toBeInTheDocument()
  })

  it('browses messages, opens one in the drawer and loads the next page', async () => {
    const cursors: (string | null)[] = []
    server.use(http.get(api('/queues/orders/messages'), ({ request }) => {
      cursors.push(new URL(request.url).searchParams.get('cursor'))
      return HttpResponse.json({ items: cursors.length === 1 ? [
        { location: '1:3:11', priority: 1, sequence: 1040011, deliveryCount: 2, sizeBytes: 30, text: '{"orderId":"A-991","total":42.1}', textTruncated: false, isBlobReference: false },
      ] : [], nextCursor: cursors.length === 1 ? '1:3:1040011' : null })
    }))
    renderAt('/queues/orders?tab=messages')
    const row = (await screen.findByText('1,040,011')).closest('tr')!
    expect(within(row).getByText('2 / 10')).toBeInTheDocument()

    await userEvent.click(row)
    const drawer = await screen.findByRole('dialog')
    expect(await within(drawer).findByText(/"orderId": "A-991"/)).toBeInTheDocument()
    expect(within(drawer).getByText('1:3:11')).toBeInTheDocument()
    await userEvent.click(within(drawer).getByRole('button', { name: 'Hex' }))
    expect(within(drawer).getByText(/00000000\s+7b 22 6f 72/)).toBeInTheDocument()
    await userEvent.click(within(drawer).getByRole('button', { name: 'Close' }))

    await userEvent.click(screen.getByRole('button', { name: /Load next 100/ }))
    await waitFor(() => expect(cursors).toEqual([null, '1:3:1040011']))
  })

  it('shows "consumed since listed" when a message has gone', async () => {
    server.use(http.get(api('/queues/orders/messages/:location'), () => new HttpResponse(null, { status: 410 })))
    renderAt('/queues/orders?tab=messages')
    await userEvent.click((await screen.findByText('1,040,015')).closest('tr')!)
    expect(await screen.findByText('Consumed since listed.')).toBeInTheDocument()
  })

  it('loads a blob payload only on request', async () => {
    let payloadCalls = 0
    server.use(http.get(api('/queues/orders/messages/:location/payload'), () => {
      payloadCalls++
      return HttpResponse.json({ contentType: 'application/json', sizeBytes: 11, contentBase64: btoa('{"big":true}'), truncated: false })
    }))
    renderAt('/queues/orders?tab=messages')
    await userEvent.click((await screen.findByText('📦 blob reference')).closest('tr')!)
    const load = await screen.findByRole('button', { name: /Load payload \(4\.2 MB\)/ })
    expect(payloadCalls).toBe(0)
    await userEvent.click(load)
    expect(await screen.findByText(/"big": true/)).toBeInTheDocument()
    expect(payloadCalls).toBe(1)
  })

  it('browses the dead-letter queue as {id}-deadletter', async () => {
    const paths: string[] = []
    server.use(http.get(api('/queues/:id/messages'), ({ request }) => {
      paths.push(new URL(request.url).pathname)
      return HttpResponse.json({ items: [], nextCursor: null })
    }))
    renderAt('/queues/orders?tab=deadletter')
    await waitFor(() => expect(paths).toContain('/api/console/queues/orders-deadletter/messages'))
  })

  it('never polls the message list', async () => {
    let calls = 0
    server.use(http.get(api('/queues/orders/messages'), () => {
      calls++
      return HttpResponse.json({ items: [], nextCursor: null })
    }))
    renderAt('/queues/orders?tab=messages')
    await screen.findByText('No ready messages.')
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    await waitFor(() => expect(calls).toBe(2))
  })

  it('lists locks with countdowns and delivery counts', async () => {
    renderAt('/queues/orders?tab=locked')
    const row = (await screen.findByText('7f3aX9c21Qa')).closest('tr')!
    expect(within(row).getByText('1 / 10')).toBeInTheDocument()
    expect(within(row).getByText('competing')).toBeInTheDocument()
    expect(within(row).getByText(/expired/)).toBeInTheDocument()
  })

  it('lists sessions', async () => {
    renderAt('/queues/orders?tab=sessions')
    expect(await screen.findByRole('link', { name: 'a' })).toHaveAttribute('href', '/queues/orders-session-a')
  })
})

describe('Topics', () => {
  it('shows subscribers with lag, breaker state and their queues', async () => {
    renderAt('/topics/news')
    const search = (await screen.findByText('search')).closest('tr')!
    expect(within(search).getByText('120')).toBeInTheDocument()
    expect(within(search).getByText(/open \(5 errors/)).toBeInTheDocument()
    expect(within(search).getByRole('link', { name: /news-sub-search/ })).toHaveAttribute('href', '/queues/news-sub-search')
    const audit = screen.getByText('audit').closest('tr')!
    expect(within(audit).getByText('● closed')).toBeInTheDocument()
  })

  it('lists topics from the catalog', async () => {
    server.use(http.get(api('/catalog'), ({ request }) =>
      HttpResponse.json(new URL(request.url).searchParams.get('kind') === 'Topic'
        ? { entries: [{ id: 'news', kind: 'Topic', registeredAt: '2026-10-12T00:00:00Z' }], nextCursor: null, totals: {} }
        : { entries: [], totals: {} })))
    renderAt('/topics')
    expect(await screen.findByRole('link', { name: 'news' })).toHaveAttribute('href', '/topics/news')
  })
})

describe('Sinks', () => {
  it('shows each sink with its queue config', async () => {
    server.use(http.get(api('/catalog'), ({ request }) =>
      HttpResponse.json(new URL(request.url).searchParams.get('kind') === 'Sink'
        ? { entries: [{ id: 'orders-sink', kind: 'Sink', parentId: 'orders', registeredAt: '2026-10-12T00:00:00Z' }], nextCursor: null, totals: {} }
        : { entries: [], totals: {} })))
    renderAt('/sinks')
    const row = (await screen.findByText('https://svc/orders')).closest('tr')!
    expect(within(row).getByText('8')).toBeInTheDocument()
    expect(within(row).getByText('30s')).toBeInTheDocument()
  })
})

describe('Shell', () => {
  it('pauses and resumes polling', async () => {
    renderAt('/')
    await userEvent.click(await screen.findByRole('button', { name: 'Pause refresh' }))
    expect(screen.getByText('Paused')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Resume refresh' }))
    expect(screen.queryByText('Paused')).not.toBeInTheDocument()
  })

  it('jumps to a queue by id', async () => {
    const { location } = renderAt('/')
    await userEvent.type(await screen.findByLabelText('Go to queue id'), 'orders{enter}')
    expect(location.current).toBe('/queues/orders')
  })
})
