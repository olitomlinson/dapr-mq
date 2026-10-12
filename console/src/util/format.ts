const integer = new Intl.NumberFormat('en-US')

export const formatCount = (n: number) => integer.format(n)

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** "24s", "3m 05s", or "expired" for a unix-seconds deadline. */
export function formatTimeLeft(deadlineSeconds: number, nowMs: number): string {
  const left = Math.floor(deadlineSeconds - nowMs / 1000)
  if (left <= 0) return 'expired'
  if (left < 60) return `${left}s`
  return `${Math.floor(left / 60)}m ${String(left % 60).padStart(2, '0')}s`
}

export const formatTime = (unixSeconds: number) => new Date(unixSeconds * 1000).toLocaleTimeString()

/** Pretty JSON if the text parses, else null. */
export function prettyJson(text: string): string | null {
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return null
  }
}

/** Hex dump, 16 bytes per line with offsets and printable ASCII. */
export function hexDump(bytes: Uint8Array, maxBytes = 64 * 1024): string {
  const lines: string[] = []
  const end = Math.min(bytes.length, maxBytes)
  for (let offset = 0; offset < end; offset += 16) {
    const row = bytes.subarray(offset, Math.min(offset + 16, end))
    const hex = Array.from(row, (b) => b.toString(16).padStart(2, '0')).join(' ')
    const ascii = Array.from(row, (b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : '.')).join('')
    lines.push(`${offset.toString(16).padStart(8, '0')}  ${hex.padEnd(47)}  ${ascii}`)
  }
  if (bytes.length > end) lines.push(`… ${formatCount(bytes.length - end)} more bytes`)
  return lines.join('\n')
}

export const utf8 = (text: string) => new TextEncoder().encode(text)

export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes
}

/** Topic id of a subscription queue ({topic}-sub-{subscriber}), or null. */
export function subscriptionTopic(queueId: string): string | null {
  const at = queueId.lastIndexOf('-sub-')
  return at > 0 ? queueId.slice(0, at) : null
}
