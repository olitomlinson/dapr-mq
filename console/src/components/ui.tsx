import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { ApiError } from '../api/client'
import s from './ui.module.css'

export { s as ui }

/** Loading / error / empty states for a query. */
export function QueryState({ isLoading, error, notFound }: { isLoading: boolean; error: unknown; notFound?: ReactNode }) {
  if (isLoading) return <p className={s.muted}>Loading…</p>
  if (error instanceof ApiError && error.status === 404 && notFound) return <p className={s.muted}>{notFound}</p>
  if (error instanceof ApiError && error.status === 503) {
    return <div className={`${s.banner} ${s.bannerDanger}`}>Workers unavailable. Retrying.</div>
  }
  if (error) return <div className={`${s.banner} ${s.bannerDanger}`}>{String((error as Error).message ?? error)}</div>
  return null
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: ReactNode }[]; value: T; onChange: (id: T) => void }) {
  return (
    <div className={s.tabs} role="tablist">
      {tabs.map((tab) => (
        <button key={tab.id} role="tab" aria-selected={tab.id === value} className={s.tab} onClick={() => onChange(tab.id)}>
          {tab.label}
        </button>
      ))}
    </div>
  )
}

export function Tile({ label, value, href }: { label: string; value: ReactNode; href?: string }) {
  const body = (
    <>
      <div className={s.tileLabel}>{label}</div>
      <div className={s.tileValue}>{value}</div>
    </>
  )
  return href ? <Link className={s.tile} to={href}>{body}</Link> : <div className={s.tile}>{body}</div>
}
