import { render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { AppRoutes } from '../App'
import { RefreshProvider } from '../api/refresh'

const location = { current: '' }

function LocationProbe() {
  const { pathname, search } = useLocation()
  location.current = pathname + search
  return null
}

/** Renders the whole app at a URL, against the MSW fixtures. `location.current` tracks navigation. */
export function renderAt(url: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <RefreshProvider pollMs={60_000}>
        <MemoryRouter initialEntries={[url]}>
          <AppRoutes />
          <LocationProbe />
        </MemoryRouter>
      </RefreshProvider>
    </QueryClientProvider>,
  )
  return { location, queryClient }
}
