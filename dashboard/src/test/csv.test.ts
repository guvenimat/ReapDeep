import { describe, expect, it } from 'vitest'
import { funnelsCsv, retentionCsv, toCsv, trendsCsv } from '../lib/csv'

describe('csv', () => {
  it('quotes separators and neutralizes spreadsheet formulas', () => {
    const csv = toCsv({ name: 'x', headers: ['a', 'b'], rows: [['x,y', 'say "hi"'], ['=SUM(A1)', '@cmd'], [-3, null], [Number.NaN, 'line\nbreak']] })

    expect(csv).toBe('a,b\r\n"x,y","say ""hi"""\r\n\'=SUM(A1),\'@cmd\r\n-3,\r\n,"line\nbreak"\r\n')
  })

  it('puts trend series side by side by bucket', () => {
    const t = trendsCsv({
      interval: 'day', timezone: 'UTC', took_ms: 1,
      results: [
        { event: 'purchase', metric: 'total', breakdown_value: 'tr', total: 3, property: null, compare: null, series: null, data: [{ bucket: '2026-11-01', count: 1 }, { bucket: '2026-11-02', count: 2 }] },
        { event: 'purchase', metric: 'total', breakdown_value: 'de', total: 1, property: null, compare: null, series: null, data: [{ bucket: '2026-11-02', count: 1 }] },
      ],
    })

    expect(t.headers).toEqual(['bucket', 'purchase · total · tr', 'purchase · total · de'])
    expect(t.rows).toEqual([['2026-11-01', 1, null], ['2026-11-02', 2, 1]])
  })

  it('writes one funnel row per breakdown and step, and the retention average last', () => {
    const f = funnelsCsv({
      window_seconds: 60, sampling: 1, took_ms: 1, times: null,
      results: [{ breakdown_value: null, steps: [{ step: 1, event: 'view', count: 10, conversion_from_first: 1, conversion_from_previous: 1 }, { step: 2, event: 'buy', count: 4, conversion_from_first: 0.4, conversion_from_previous: 0.4 }] }],
    })
    const r = retentionCsv({ period: 'day', periods: 2, timezone: 'UTC', took_ms: 1, cohorts: [{ cohort_start: '2026-11-01', size: 5, counts: [5, 2] }], average: [1, 0.4] })

    expect(f.rows).toEqual([['', 1, 'view', 10, 1, 1], ['', 2, 'buy', 4, 0.4, 0.4]])
    expect(r.headers).toEqual(['cohort_start', 'size', 'day_0', 'day_1'])
    expect(r.rows.at(-1)).toEqual(['average', null, 1, 0.4])
  })
})
