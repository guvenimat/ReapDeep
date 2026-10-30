import { api } from '../../api/client'
import type { Breakdown, Filter, Interval, Metric, Series, TrendResponse, TrendResult } from '../../api/types'
import { defaultInterval, readState, toDateRange, type Range } from '../../app/route'
import { Legend, MAX_SERIES, seriesColor } from '../../components/charts/common'
import { TimeChart, type ChartSeries } from '../../components/charts/TimeChart'
import { AddButton, BreakdownEditor, completeBreakdown, completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Card, IconButton, Segmented, StatTile, Status } from '../../components/ui'
import { bucketLabel, ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'

export interface TrendState {
  series: Series[]
  interval?: Interval
  filters: Filter[]
  breakdown?: Breakdown
  deduplicate: boolean
  chart: 'line' | 'bar'
  formula?: string
  compare?: boolean
}

export const TREND_DEFAULT: TrendState = { series: [{ event: '$pageview', metric: 'total' }], filters: [], deduplicate: false, chart: 'line' }
const METRICS: { id: Metric; label: string; property?: boolean }[] = [
  { id: 'total', label: 'Total events' },
  { id: 'unique_users', label: 'Unique users' },
  { id: 'sum', label: 'Sum of', property: true },
  { id: 'avg', label: 'Average of', property: true },
  { id: 'median', label: 'Median of', property: true },
  { id: 'p90', label: '90th percentile of', property: true },
  { id: 'min', label: 'Minimum of', property: true },
  { id: 'max', label: 'Maximum of', property: true },
]
const needsProperty = (m: Metric) => METRICS.find((x) => x.id === m)?.property === true
export const seriesLetter = (i: number) => String.fromCharCode(65 + i)
const INTERVALS: { id: Interval; label: string }[] = [
  { id: 'hour', label: 'Hour' }, { id: 'day', label: 'Day' }, { id: 'week', label: 'Week' }, { id: 'month', label: 'Month' },
]

export const trendInterval = (state: TrendState, range: Range) => state.interval ?? defaultInterval(range.preset)

export function trendBody(state: TrendState, range: Range) {
  return {
    series: state.series.filter((s) => s.event.trim()).map((s) => (needsProperty(s.metric) ? s : { event: s.event, metric: s.metric })),
    interval: trendInterval(state, range),
    filters: completeFilters(state.filters),
    breakdown: completeBreakdown(state.breakdown),
    deduplicate: state.deduplicate,
    formula: state.formula?.trim() || undefined,
    compare: state.compare || undefined,
    ...toDateRange(range),
  }
}

export function trendLabel(r: TrendResult) {
  if (r.metric === 'formula') return [`Formula: ${r.event}`, r.breakdown_value, r.compare && 'previous period'].filter(Boolean).join(' · ')
  const m = METRICS.find((x) => x.id === r.metric)
  const metric = m?.property ? `${m.label} ${r.property}` : m?.label
  return [r.event, metric, r.breakdown_value, r.compare && 'previous period'].filter(Boolean).join(' · ')
}

export function visibleResults(data?: TrendResponse) {
  const results = data?.results ?? []
  return results.some((r) => r.metric === 'formula') ? results.filter((r) => r.metric === 'formula') : results
}

const sameSeries = (a: TrendResult, b: TrendResult) => a.series === b.series && a.breakdown_value === b.breakdown_value

export function trendChartSeries(data?: TrendResponse): ChartSeries[] {
  const visible = visibleResults(data)
  const current = visible.filter((r) => !r.compare).slice(0, MAX_SERIES)
  const single = new Set(current.map((r) => `${r.event}|${r.metric}|${r.property}`)).size === 1
  const label = (r: TrendResult) => (single && r.breakdown_value !== null ? r.breakdown_value : trendLabel(r))
  const series: ChartSeries[] = current.map((r, i) => ({ key: `${i}`, label: label(r), colorIndex: i, values: r.data.map((d) => d.count) }))
  visible.filter((r) => r.compare).forEach((r, k) => {
    const i = current.findIndex((c) => sameSeries(c, r))
    if (i >= 0) series.push({ key: `p${k}`, label: `${label(r).replace(/ · previous period$/, '')} (previous)`, colorIndex: i, values: r.data.map((d) => d.count), dashed: true })
  })
  return series
}

const change = (now: number, before: number) => (before === 0 ? null : (now - before) / before)

export function Trends({ params, range, onChange, onInsight }: {
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
}) {
  const state = readState(params, TREND_DEFAULT)
  const set = (patch: Partial<TrendState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const setSeries = (i: number, patch: Partial<Series>) => set({ series: state.series.map((x, k) => (k === i ? { ...x, ...patch } : x)) })
  const interval = trendInterval(state, range)

  const request = useDebounced(JSON.stringify(trendBody(state, range)))
  const q = useQuery((signal) => {
    const body = JSON.parse(request)
    return body.series.length ? api.trends(body, signal) : Promise.reject(new Error('Add at least one series'))
  }, [request])

  const results = q.data?.results ?? []
  const visible = visibleResults(q.data)
  const current = visible.filter((r) => !r.compare)
  const buckets = current[0]?.data.map((d) => d.bucket) ?? []
  const chartSeries = trendChartSeries(q.data)
  const previousOf = (r: TrendResult) => visible.find((p) => p.compare && sameSeries(p, r))

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Series">
          {state.series.map((s, i) => (
            <div className="editor-item" key={i}>
              <div className="row">
                <span className="series-letter" aria-label={`Series ${seriesLetter(i)}`}>{seriesLetter(i)}</span>
                <input list="dl-events" placeholder="event" value={s.event} aria-label={`Series ${i + 1} event`}
                  onChange={(e) => setSeries(i, { event: e.target.value })} />
                {state.series.length > 1 && (
                  <IconButton icon="close" label="Remove series" onClick={() => set({ series: state.series.filter((_, k) => k !== i) })} />
                )}
              </div>
              <div className="row">
                <select value={s.metric} onChange={(e) => setSeries(i, { metric: e.target.value as Metric })} aria-label={`Series ${i + 1} metric`}
                  className={needsProperty(s.metric) ? 'op' : undefined}>
                  {METRICS.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}
                </select>
                {needsProperty(s.metric) && (
                  <input list="dl-event-props" placeholder="numeric property" value={s.property ?? ''} aria-label={`Series ${i + 1} property`}
                    onChange={(e) => setSeries(i, { property: e.target.value })} />
                )}
              </div>
            </div>
          ))}
          {state.series.length < 10 && <AddButton onClick={() => set({ series: [...state.series, { event: '', metric: 'total' }] })}>Add series</AddButton>}
        </EditorSection>

        <EditorSection title="Formula" hint="Combine series by letter, for example A / B for revenue per buyer. The chart then shows the formula.">
          <input placeholder="e.g. A / B * 100" value={state.formula ?? ''} onChange={(e) => set({ formula: e.target.value })} aria-label="Formula"
            data-testid="trend-formula" />
        </EditorSection>

        <EditorSection title="Display">
          <div className="row">
            <Segmented<Interval> size="sm" label="Interval" value={interval} options={INTERVALS} onChange={(v) => set({ interval: v })} />
          </div>
          <div className="row">
            <Segmented<'line' | 'bar'> size="sm" label="Chart type" value={state.chart}
              options={[{ id: 'line', label: 'Line' }, { id: 'bar', label: 'Bar' }]} onChange={(chart) => set({ chart })} />
          </div>
          <label className="switch">
            <input type="checkbox" checked={state.deduplicate} onChange={(e) => set({ deduplicate: e.target.checked })} />
            <span className="switch-track" aria-hidden />
            Count duplicate events once
          </label>
          <label className="switch">
            <input type="checkbox" checked={!!state.compare} onChange={(e) => set({ compare: e.target.checked })} />
            <span className="switch-track" aria-hidden />
            Compare to previous period
          </label>
        </EditorSection>

        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
        <BreakdownEditor value={state.breakdown} onChange={(breakdown) => set({ breakdown })} allowCohort />
      </aside>

      <section className="content">
        <SaveInsight kind="trends" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={results.length === 0 || results.every((r) => r.total === 0)}>
          {current.length <= 4 && (
            <div className="stats">
              {current.map((r, i) => {
                const peak = r.data.reduce((best, d) => (d.count > best.count ? d : best), r.data[0] ?? { bucket: '', count: 0 })
                const prev = previousOf(r)
                const delta = prev ? change(r.total, prev.total) : null
                return (
                  <StatTile key={i} swatch={seriesColor(i)} label={chartSeries[i]?.label ?? trendLabel(r)} value={num(r.total)} testId={`trend-total-${i}`}
                    hint={prev
                      ? (delta === null ? `Previous period: ${num(prev.total)}` : `${delta >= 0 ? '+' : '−'}${pct(Math.abs(delta))} vs previous period (${num(prev.total)})`)
                      : peak.count > 0 ? `Peak ${num(peak.count)} · ${bucketLabel(peak.bucket, interval)}` : 'No events'} />
                )
              })}
            </div>
          )}

          <Card testId="trends-chart" title="Over time" meta={`${q.data?.timezone ?? ''} · ${ms(q.data?.took_ms ?? 0)}`}>
            <Legend items={chartSeries.map((s) => ({ label: s.label, colorIndex: s.colorIndex, dashed: s.dashed }))} />
            <TimeChart buckets={buckets} interval={interval} series={chartSeries} mode={state.chart} />
            {current.length > MAX_SERIES && <p className="footnote">The chart shows the first {MAX_SERIES} series; the table has all of them.</p>}
            {state.compare && <p className="footnote">Dashed lines are the previous period of the same length, aligned bucket by bucket.</p>}
          </Card>

          <Card title="Breakdown table">
            <div className="table-wrap">
              <table data-testid="trends-table">
                <thead>
                  <tr>
                    <th>Series</th>
                    <th className="num">Total</th>
                    {buckets.map((b) => <th key={b} className="num">{bucketLabel(b, interval)}</th>)}
                  </tr>
                </thead>
                <tbody>
                  {results.map((r, i) => (
                    <tr key={i} className={r.compare ? 'muted-row' : undefined}>
                      <td className="nowrap sticky-col">
                        {r.series && r.series !== 'formula' && <span className="series-letter sm">{r.series}</span>}
                        {trendLabel(r)}
                      </td>
                      <td className="num strong">{num(r.total)}</td>
                      {r.data.map((d) => <td key={d.bucket} className="num">{num(d.count)}</td>)}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="footnote">For unique users, the period total is not the sum of the buckets (the same person can appear in several buckets).</p>
          </Card>
        </Status>
      </section>
    </div>
  )
}
