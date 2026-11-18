import { describe, expect, it } from 'vitest'
import { change, pointChange, valueLabel } from '../features/web/Web'

describe('web analytics helpers', () => {
  it('compares a metric with the previous period', () => {
    expect(change(120, 100)).toBe('+20%')
    expect(change(75, 100)).toBe('−25%')
    expect(change(5, 0)).toBe('new')
    expect(change(0, 0)).toBe('no change')
  })

  it('compares rates in percentage points', () => {
    expect(pointChange(0.5, 0.4)).toBe('+10 pp')
    expect(pointChange(0.25, 0.375)).toBe('−12.5 pp')
    expect(pointChange(null, 0.4)).toBe('—')
  })

  it('labels missing values and direct traffic', () => {
    expect(valueLabel('referring_domain', '$direct')).toBe('Direct')
    expect(valueLabel('referring_domain', null)).toBe('Direct')
    expect(valueLabel('utm_source', null)).toBe('(none)')
    expect(valueLabel('pathname', '/pricing')).toBe('/pricing')
  })
})
