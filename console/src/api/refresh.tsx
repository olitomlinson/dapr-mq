import { createContext, useContext, useState, type ReactNode } from 'react'

export const DEFAULT_POLL_MS = 5000

interface Refresh {
  pollMs: number
  paused: boolean
  setPaused: (paused: boolean) => void
}

const RefreshContext = createContext<Refresh>({ pollMs: DEFAULT_POLL_MS, paused: false, setPaused: () => {} })

/** Polling for list, detail and metrics views. Browsing (messages, locks, sessions) is never polled. */
export function RefreshProvider({ children, pollMs = DEFAULT_POLL_MS }: { children: ReactNode; pollMs?: number }) {
  const [paused, setPaused] = useState(false)
  return <RefreshContext.Provider value={{ pollMs, paused, setPaused }}>{children}</RefreshContext.Provider>
}

export function useRefresh() {
  return useContext(RefreshContext)
}

/** refetchInterval for a polled query. */
export function usePollInterval(): number | false {
  const { pollMs, paused } = useRefresh()
  return paused ? false : pollMs
}
