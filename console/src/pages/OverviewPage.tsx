import { useCatalog } from '../api/hooks'
import { formatCount } from '../util/format'
import { QueryState, Tile, ui } from '../components/ui'

export function OverviewPage() {
  const catalog = useCatalog(null, null, 1)
  const totals = catalog.data?.totals ?? {}
  const count = (n?: number) => (catalog.data ? formatCount(n ?? 0) : '–')

  return (
    <div className={ui.page}>
      <h1 className={ui.title}>Overview</h1>
      <QueryState isLoading={catalog.isLoading} error={catalog.error} />
      <div className={ui.tiles}>
        <Tile label="Queues" value={count(totals.Queue)} href="/queues" />
        <Tile label="Dead-letter queues" value={count(totals.DeadLetter)} href="/queues?kind=DeadLetter" />
        <Tile label="Topics" value={count(totals.Topic)} href="/topics" />
        <Tile label="HTTP sinks" value={count(totals.Sink)} href="/sinks" />
      </div>
      <p className={`${ui.muted} ${ui.small}`}>
        Queues are listed once they first activate. Everything here is read from committed state; browsing never consumes or locks a message.
      </p>
    </div>
  )
}
