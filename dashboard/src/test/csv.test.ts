import { describe, expect, it } from 'vitest'
import { experimentCsv, funnelsCsv, retentionCsv, stickinessCsv, toCsv, trendsCsv, webCsv } from '../lib/csv'

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

  it('exports stickiness, web and experiment results', () => {
    const sticky = stickinessCsv({
      event: 'purchase', interval: 'day', total_periods: 2, persons: 5, average_periods: 1.4, timezone: 'UTC',
      buckets: [{ periods: 1, persons: 4, share: 0.8 }, { periods: 2, persons: 1, share: 0.2 }], took_ms: 1,
    })
    const web = webCsv({
      interval: 'day', timezone: 'UTC', date_from: '2026-11-01', date_to: '2026-11-02', previous_from: '2026-10-31',
      current: { visitors: 3, pageviews: 5, sessions: 2, bounces: 1, bounce_rate: 0.5, avg_session_seconds: 30 },
      previous: { visitors: 0, pageviews: 0, sessions: 0, bounces: 0, bounce_rate: null, avg_session_seconds: null },
      series: [{ bucket: '2026-11-01', visitors: 2, pageviews: 3 }], pages: [], breakdowns: [], took_ms: 1,
    })
    const experiment = experimentCsv({
      experiment_id: 1, metric_kind: 'conversion', test: 'two_proportion_z', control_variant: 'control',
      date_from: '2026-11-01', date_to: '2026-11-02', excluded_persons: 0, srm: null, took_ms: 1,
      variants: [{
        key: 'control', percentage: 50, is_control: true, persons: 10, conversions: 2, mean: 0.2, standard_deviation: null, comparison: null,
      }],
    })

    expect(sticky.rows).toEqual([[1, 4, 0.8], [2, 1, 0.2]])
    expect(web.rows).toEqual([['period total', 3, 5], ['2026-11-01', 2, 3]])
    expect(experiment.rows[0]).toEqual(['control', 50, true, 10, 2, 0.2, null, null, null, null, null, null, null, null, null, null])
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

describe('annotations', () => {
  it('places a note on the bucket that contains its day', async () => {
    const { annotationIndex } = await import('../features/insights/Annotations')
    const days = ['2026-11-01', '2026-11-02', '2026-11-03']

    expect(annotationIndex(days, 'day', '2026-11-02')).toBe(1)
    expect(annotationIndex(days, 'day', '2026-11-04')).toBe(-1)
    expect(annotationIndex(['2026-11-02 00:00', '2026-11-02 01:00', '2026-11-03 00:00'], 'hour', '2026-11-03')).toBe(2)
    expect(annotationIndex(['2026-10-26', '2026-11-02'], 'week', '2026-11-05')).toBe(1)
    expect(annotationIndex(['2026-10-26', '2026-11-02'], 'week', '2026-11-09')).toBe(-1)
    expect(annotationIndex(['2026-10-01', '2026-11-01'], 'month', '2026-11-30')).toBe(1)
  })
})
