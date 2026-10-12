/** A non-2xx reply from /api/console. 404 and 410 are expected outcomes, not failures. */
export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
  }
}

export async function getJson<T>(path: string, params: Record<string, string | number | null | undefined> = {}): Promise<T> {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== '') {
      query.set(key, String(value))
    }
  }
  const qs = query.toString()
  const response = await fetch(`/api/console${path}${qs ? `?${qs}` : ''}`)
  if (!response.ok) {
    throw new ApiError(response.status, `${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

/** Escapes one path segment (queue ids, locations, lock ids). */
export const seg = encodeURIComponent
