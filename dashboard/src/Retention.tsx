import { api, type Filter, type RetentionResponse } from './api'
import { dateTime, ms, num, pct } from './format'
import { SaveInsight } from './Insights'
import { readState, toDateRange, type rangeFromParams } from './route'
import { completeFilters, FilterEditor, Status, useDebounced, useQuery } from './ui'

type Range = ReturnType<typeof rangeFromParams>

export interface RetentionState { start_event: string; return_event: string; period: 'day' | 'week'; periods: number; filters: Filter[] }

export const RETENTION_DEFAULT: RetentionState = { start_event: '$identify', return_event: '$pageview', period: 'week', periods: 6, filters: [] }

export function retentionBody(state: RetentionState, range: Range) {
  return {
    start_event: state.start_event.trim(),
    return_event: state.return_event.trim() || undefined,
    period: state.period,
    periods: state.periods,
    filters: completeFilters(state.filters),
    ...toDateRange(range),
  }
}

const heatClass = (ratio: number) => (ratio <= 0 ? 'heat-0' : `heat-${Math.min(6, Math.ceil(ratio * 6))}`)

export function RetentionTable({ data, maxCohorts }: { data: RetentionResponse; maxCohorts?: number }) {
  const label = data.period === 'week' ? 'Week' : 'Day'
  const cohorts = maxCohorts ? data.cohorts.slice(-maxCohorts) : data.cohorts
  const cohortLabel = (iso: string) => new Date(iso).toLocaleDateString('en-US', { day: '2-digit', month: 'short' })
  return (
    <div className="table-wrap">
      <table className="retention" data-testid="retention-table">
        <thead>
          <tr>
            <th>Cohort</th>
            <th className="num">Persons</th>
            {data.average.map((_, k) => <th key={k} className="num">{label} {k}</th>)}
          </tr>
        </thead>
        <tbody>
          <tr className="retention-average">
            <td className="strong">Average</td>
            <td className="num">{num(data.cohorts.reduce((s, c) => s + c.size, 0))}</td>
            {data.average.map((a, k) => (
              <td key={k} className={`num heat ${a === null ? 'heat-none' : heatClass(a)}`}>{a === null ? '' : pct(a)}</td>
            ))}
          </tr>
          {cohorts.map((c) => (
            <tr key={c.cohort_start}>
              <td className="nowrap">{cohortLabel(c.cohort_start)}</td>
              <td className="num">{num(c.size)}</td>
              {c.counts.map((n, k) => {
                const ratio = n === null || c.size === 0 ? null : n / c.size
                return (
                  <td key={k} className={`num heat ${ratio === null ? 'heat-none' : heatClass(ratio)}`}
                    title={ratio === null ? 'Not reached yet' : `${num(n!)} of ${num(c.size)} persons (${pct(ratio)}) · ${label.toLowerCase()} ${k} after ${dateTime(c.cohort_start)}`}>
                    {ratio === null ? '' : c.size === 0 ? '-' : pct(ratio)}
                  </td>
                )
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function Retention({ params, range, onChange, onInsight }: {
  params: URLSearchParams
  range: Range
  onChange: (q: string) => void
  onInsight: (id?: string) => void
}) {
  const state = readState(params, RETENTION_DEFAULT)
  const set = (patch: Partial<RetentionState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(retentionBody(state, range)))
  const q = useQuery((signal) => {
    const body = JSON.parse(request)
    return body.start_event ? api.retention(body, signal) : Promise.reject(new Error('Choose a start event'))
  }, [request])

  return (
    <div className="layout">
      <aside className="panel">
        <div className="editor-block">
          <div className="editor-title">Cohort</div>
          <label className="field"><span>People who did</span>
            <input list="dl-events" value={state.start_event} onChange={(e) => set({ start_event: e.target.value })} aria-label="Start event" />
          </label>
          <label className="field"><span>and came back to do</span>
            <input list="dl-events" value={state.return_event} placeholder="same event" onChange={(e) => set({ return_event: e.target.value })} aria-label="Return event" />
          </label>
        </div>
        <div className="editor-block">
          <div className="editor-title">Periods</div>
          <div className="row">
            <div className="segmented" role="group" aria-label="Period">
              <button className={state.period === 'day' ? 'active' : ''} onClick={() => set({ period: 'day', periods: Math.min(state.periods * 7, 14) })}>Daily</button>
              <button className={state.period === 'week' ? 'active' : ''} onClick={() => set({ period: 'week', periods: Math.max(1, Math.min(Math.round(state.periods / 7) || 6, 12)) })}>Weekly</button>
            </div>
            <label className="inline">Show
              <input type="number" className="narrow" min={1} max={30} value={state.periods}
                onChange={(e) => set({ periods: Math.max(1, Math.min(30, Number(e.target.value) || 1)) })} aria-label="Number of periods" />
            </label>
          </div>
          <p className="muted small">Cohorts are formed from the first time a person did the start event in the selected date range.</p>
        </div>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="retention" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={q.data?.cohorts.every((c) => c.size === 0)}>
          <div className="card" data-testid="retention-card">
            <div className="card-head">
              <h2>Retention</h2>
              <span className="muted">{q.data?.timezone} · {ms(q.data?.took_ms ?? 0)}</span>
            </div>
            {q.data && <RetentionTable data={q.data} />}
            <p className="muted small">Each cell is the share of the cohort that did the return event in that period; period 0 is the cohort's own period.</p>
          </div>
        </Status>
      </section>
    </div>
  )
}
