import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useCatalog } from '../api/hooks'
import { seg } from '../api/client'
import { QueryState, ui } from '../components/ui'

export function TopicsPage() {
  const [cursors, setCursors] = useState<(string | null)[]>([null])
  const catalog = useCatalog('Topic', cursors[cursors.length - 1], 100)

  return (
    <div className={ui.page}>
      <h1 className={ui.title}>Topics</h1>
      <QueryState isLoading={catalog.isLoading} error={catalog.error} />
      {catalog.data && (
        <div className={ui.tableWrap}>
          <table className={ui.table}>
            <thead><tr><th>Topic</th><th>Registered</th></tr></thead>
            <tbody>
              {catalog.data.entries.map((topic) => (
                <tr key={topic.id}>
                  <td><Link to={`/topics/${seg(topic.id)}`}>{topic.id}</Link></td>
                  <td className={ui.muted}>{new Date(topic.registeredAt).toLocaleString()}</td>
                </tr>
              ))}
              {catalog.data.entries.length === 0 && <tr><td colSpan={2} className={ui.muted}>No topics.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      <div className={ui.toolbar}>
        <button className={ui.button} disabled={cursors.length === 1} onClick={() => setCursors(cursors.slice(0, -1))}>‹ Previous</button>
        <button className={ui.button} disabled={!catalog.data?.nextCursor} onClick={() => setCursors([...cursors, catalog.data!.nextCursor!])}>Next ›</button>
      </div>
    </div>
  )
}
