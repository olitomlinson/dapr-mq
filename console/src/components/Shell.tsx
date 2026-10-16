import { useState, type FormEvent } from 'react'
import { NavLink, Outlet, Link, useNavigate } from 'react-router-dom'
import { useCatalog } from '../api/hooks'
import { useRefresh } from '../api/refresh'
import { seg } from '../api/client'
import s from './Shell.module.css'
import { ui } from './ui'

const links = [
  { to: '/', label: 'Overview', end: true },
  { to: '/queues', label: 'Queues' },
  { to: '/topics', label: 'Topics' },
  { to: '/sinks', label: 'Sinks' },
  { to: '/scaling', label: 'Scaling' },
]

export function Shell() {
  const { pollMs, paused, setPaused } = useRefresh()
  // The overview's catalog read doubles as the worker health signal.
  const health = useCatalog(null, null, 1)
  const navigate = useNavigate()
  const [target, setTarget] = useState('')

  const go = (event: FormEvent) => {
    event.preventDefault()
    const id = target.trim()
    if (id) navigate(`/queues/${seg(id)}`)
  }

  return (
    <div className={s.shell}>
      <header className={s.header}>
        <Link to="/" className={s.brand}>DaprMQ Console</Link>
        <form className={s.goto} onSubmit={go} role="search">
          <input
            className={ui.input}
            placeholder="Go to queue id…"
            aria-label="Go to queue id"
            value={target}
            onChange={(e) => setTarget(e.target.value)}
          />
        </form>
        <div className={s.refresh}>
          <span>{paused ? 'Paused' : `⟳ ${pollMs / 1000}s`}</span>
          <button className={ui.button} onClick={() => setPaused(!paused)} aria-label={paused ? 'Resume refresh' : 'Pause refresh'}>
            {paused ? '▶' : '⏸'}
          </button>
        </div>
      </header>
      <nav className={s.nav} aria-label="Main">
        {links.map((link) => (
          <NavLink key={link.to} to={link.to} end={link.end} className={s.navLink}>
            {link.label}
          </NavLink>
        ))}
        <div className={s.status}>
          Workers
          <span className={`${s.dot} ${health.isError ? s.down : s.up}`} aria-label={health.isError ? 'unreachable' : 'reachable'} />
        </div>
      </nav>
      <main className={s.main}>
        <Outlet />
      </main>
    </div>
  )
}
