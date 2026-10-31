import { describe, expect, it } from 'vitest'
import { bucketLabel, compact, initials, niceTicks, pct, shortId } from '../lib/format'

describe('format', () => {
  it('formats buckets for each interval', () => {
    expect(bucketLabel('2026-10-31T00:00:00', 'day')).toBe('31 Oct')
    expect(bucketLabel('2026-10-31T00:00:00', 'day', true)).toBe('31 Oct 2026')
    expect(bucketLabel('2026-10-31T14:00:00', 'hour')).toBe('14:00')
    expect(bucketLabel('2026-10-01T00:00:00', 'month')).toBe('Oct 2026')
    expect(bucketLabel('2026-10-26T00:00:00', 'week', true)).toBe('Week of Oct 26')
  })

  it('formats numbers compactly and as percentages', () => {
    expect(compact(950)).toBe('950')
    expect(compact(12_500)).toBe('12.5K')
    expect(pct(0.1234)).toBe('12.3%')
  })

  it('builds round axis ticks that cover the maximum', () => {
    const ticks = niceTicks(97)
    expect(ticks[0]).toBe(0)
    expect(ticks.at(-1)).toBeGreaterThanOrEqual(97)
  })

  it('shortens long ids and builds initials', () => {
    expect(shortId('01a192fd-b28d-7ccf-afff-dfd467e52ce6')).toBe('01a192fd…2ce6')
    expect(shortId('customer-42')).toBe('customer-42')
    expect(initials('Ada Lovelace')).toBe('AL')
  })
})
