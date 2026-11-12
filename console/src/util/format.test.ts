import { describe, expect, it } from 'vitest'
import { formatBytes, formatDuration, formatExpiresIn, formatTimeLeft, hexDump, prettyJson, subscriptionTopic, utf8 } from './format'

describe('format', () => {
  it('formats time left', () => {
    expect(formatTimeLeft(100, 76_000)).toBe('24s')
    expect(formatTimeLeft(300, 114_500)).toBe('3m 05s')
    expect(formatTimeLeft(100, 100_000)).toBe('expired')
  })

  it('formats a duration as its two largest units', () => {
    expect(formatDuration(45)).toBe('45s')
    expect(formatDuration(600)).toBe('10m')
    expect(formatDuration(3725)).toBe('1h 2m')
    expect(formatDuration(2_419_200)).toBe('28d')
    expect(formatDuration(31_536_000)).toBe('365d')
    expect(formatDuration(90_061)).toBe('1d 1h')
  })

  it('formats time until a message expires', () => {
    expect(formatExpiresIn(10_000, 6_400_000)).toBe('1h')
    expect(formatExpiresIn(10_000, 10_000_000)).toBe('expired')
  })

  it('formats bytes', () => {
    expect(formatBytes(312)).toBe('312 B')
    expect(formatBytes(4404019)).toBe('4.2 MB')
  })

  it('dumps hex with offsets and ascii', () => {
    expect(hexDump(utf8('{"a":1}'))).toBe('00000000  7b 22 61 22 3a 31 7d                             {"a":1}')
  })

  it('pretty-prints only valid JSON', () => {
    expect(prettyJson('{"a":1}')).toBe('{\n  "a": 1\n}')
    expect(prettyJson('not json')).toBeNull()
  })

  it('finds the topic of a subscription queue', () => {
    expect(subscriptionTopic('news-sub-audit')).toBe('news')
    expect(subscriptionTopic('orders')).toBeNull()
  })
})
