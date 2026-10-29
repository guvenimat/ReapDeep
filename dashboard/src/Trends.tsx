import { api, type Breakdown, type Filter, type Interval, type Metric, type Series, type TrendResponse } from './api'
import { Legend, MAX_SERIES, TimeChart, type ChartSeries } from './charts'
import { bucketLabel, ms, num } from './format'
import { defaultInterval, readState, toDateRange, type rangeFromParams } from './route'
import { SaveInsight } from './Insights'
import { BreakdownEditor, completeBreakdown, completeFilters, FilterEditor, Status, useDebounced, useQuery } from './ui'

export interface TrendState {
  series: Series[]
  interval?: Interval
  filters: Filter[]
  breakdown?: Breakdown
  deduplicate: boolean
  chart: 'line' | 'bar'
}

export const TREND_DEFAULT: TrendState = { series: [{ event: '$pageview', metric: 'total' }], filters: [], deduplicate: false, chart: 'line' }
const METRICS: { id: Metric; label: string }[] = [
  { id: 'total', label: 'Total events' },
  { id: 'unique_users', label: 'Unique users' },
]
const INTERVALS: { id: Interval; label: string }[] = [
  { id: 'hour', label: 'Hour' }, { id: 'day', label: 'Day' }, { id: 'week', label: 'Week' }, { id: 'month', label: 'Month' },
]

type Range = ReturnType<typeof rangeFromParams>

export const trendInterval = (state: TrendState, range: Range) => state.interval ?? defaultInterval(range.preset)

export function trendBody(state: TrendState, range: Range) {
  return {
    series: state.series.filter((s) => s.event.trim()),
    interval: trendInterval(state, range),
    filters: completeFilters(state.filters),
    breakdown: completeBreakdown(state.breakdown),
    deduplicate: state.deduplicate,
    ...toDateRange(range),
  }
}

export const trendLabel = (r: TrendResponse['results'][number]) =>
  [r.event, METRICS.find((m) => m.id === r.metric)?.label, r.breakdown_value].filter(Boolean).join(' · ')

export function trendChartSeries(data?: TrendResponse): ChartSeries[] {
  const results = data?.results ?? []
  const single = new Set(results.map((r) => `${r.event}|${r.metric}`)).size === 1
  return results.slice(0, MAX_SERIES).map((r, i) => ({
    key: `${i}`, label: single && r.breakdown_value !== null ? r.breakdown_value : trendLabel(r), colorIndex: i, values: r.data.map((d) => d.count),
  }))
}

export function Trends({ params, range, onChange, onInsight }: {
  params: URLSearchParams
  range: ReturnType<typeof rangeFromParams>
  onChange: (q: string) => void
  onInsight: (id?: string) => void
}) {
  const state = readState(params, TREND_DEFAULT)
  const set = (patch: Partial<TrendState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const interval = trendInterval(state, range)

  const request = useDebounced(JSON.stringify(trendBody(state, range)))
  const q = useQuery((signal) => {
    const body = JSON.parse(request)
    return body.series.length ? api.trends(body, signal) : Promise.reject(new Error('Add at least one series'))
  }, [request])

  const results = q.data?.results ?? []
  const buckets = results[0]?.data.map((d) => d.bucket) ?? []
  const label = trendLabel
  const chartSeries = trendChartSeries(q.data)

  return (
    <div className="layout">
      <aside className="panel">
        <div className="editor-block">
          <div className="editor-title">Series</div>
          {state.series.map((s, i) => (
            <div className="row" key={i}>
              <span className="series-key" style={{ background: `var(--series-${(i % MAX_SERIES) + 1})` }} aria-hidden />
              <input list="dl-events" placeholder="event" value={s.event} aria-label={`Series ${i + 1} event`}
                onChange={(e) => set({ series: state.series.map((x, k) => (k === i ? { ...x, event: e.target.value } : x)) })} />
              <select value={s.metric} aria-label={`Series ${i + 1} metric`}
                onChange={(e) => set({ series: state.series.map((x, k) => (k === i ? { ...x, metric: e.target.value as Metric } : x)) })}>
                {METRICS.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}
              </select>
              {state.series.length > 1 && (
                <button className="icon" aria-label="Remove series" onClick={() => set({ series: state.series.filter((_, k) => k !== i) })}>×</button>
              )}
            </div>
          ))}
          {state.series.length < 10 && (
            <button className="link" onClick={() => set({ series: [...state.series, { event: '', metric: 'total' }] })}>+ Add series</button>
          )}
        </div>

        <div className="editor-block">
          <div className="editor-title">Settings</div>
          <div className="row">
            <label className="inline">Interval
              <select value={interval} onChange={(e) => set({ interval: e.target.value as Interval })}>
                {INTERVALS.map((x) => <option key={x.id} value={x.id}>{x.label}</option>)}
              </select>
            </label>
            <div className="segmented" role="group" aria-label="Chart type">
              <button className={state.chart === 'line' ? 'active' : ''} onClick={() => set({ chart: 'line' })}>Line</button>
              <button className={state.chart === 'bar' ? 'active' : ''} onClick={() => set({ chart: 'bar' })}>Bar</button>
            </div>
          </div>
          <label className="inline check">
            <input type="checkbox" checked={state.deduplicate} onChange={(e) => set({ deduplicate: e.target.checked })} />
            Count duplicate events once (event_id)
          </label>
        </div>

        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
        <BreakdownEditor value={state.breakdown} onChange={(breakdown) => set({ breakdown })} />
      </aside>

      <section className="content">
        <SaveInsight kind="trends" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={results.length === 0 || results.every((r) => r.total === 0)}>
          <div className="card" data-testid="trends-chart">
            <div className="card-head">
              <h2>Trend</h2>
              <span className="muted">{q.data?.timezone} · {ms(q.data?.took_ms ?? 0)}</span>
            </div>
            <Legend items={chartSeries.map((s) => ({ label: s.label, colorIndex: s.colorIndex }))} />
            <TimeChart buckets={buckets} interval={interval} series={chartSeries} mode={state.chart} />
            {results.length > MAX_SERIES && <p className="muted small">The chart shows the first {MAX_SERIES} series; the table has all of them.</p>}
          </div>

          <div className="card">
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
                    <tr key={i}>
                      <td className="nowrap">
                        {i < MAX_SERIES && <span className="swatch" style={{ background: `var(--series-${i + 1})` }} />}
                        {label(r)}
                      </td>
                      <td className="num strong">{num(r.total)}</td>
                      {r.data.map((d) => <td key={d.bucket} className="num">{num(d.count)}</td>)}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="muted small">For unique users, the period total is not the sum of the buckets (the same person can appear in several buckets).</p>
          </div>
        </Status>
      </section>
    </div>
  )
}
