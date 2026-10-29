import { api } from '../../api/client'
import type { Filter, RetentionResponse } from '../../api/types'
import { readState, toDateRange, type Range } from '../../app/route'
import { completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Card, Segmented, StatTile, Status } from '../../components/ui'
import { dateTime, ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'

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
              <td key={k} className={`num heat ${a === null ? 'heat-none' : heatClass(a)}`}><span>{a === null ? '' : pct(a)}</span></td>
            ))}
          </tr>
          {cohorts.map((c) => (
            <tr key={c.cohort_start}>
              <td className="nowrap">{cohortLabel(c.cohort_start)}</td>
              <td className="num muted">{num(c.size)}</td>
              {c.counts.map((n, k) => {
                const ratio = n === null || c.size === 0 ? null : n / c.size
                return (
                  <td key={k} className={`num heat ${ratio === null ? 'heat-none' : heatClass(ratio)}`}
                    title={ratio === null ? 'Not reached yet' : `${num(n!)} of ${num(c.size)} persons (${pct(ratio)}) · ${label.toLowerCase()} ${k} after ${dateTime(c.cohort_start)}`}>
                    <span>{ratio === null ? '' : c.size === 0 ? '-' : pct(ratio)}</span>
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
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
}) {
  const state = readState(params, RETENTION_DEFAULT)
  const set = (patch: Partial<RetentionState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(retentionBody(state, range)))
  const q = useQuery((signal) => {
    const body = JSON.parse(request)
    return body.start_event ? api.retention(body, signal) : Promise.reject(new Error('Choose a start event'))
  }, [request])

  const unit = state.period === 'week' ? 'Week' : 'Day'
  const avg = q.data?.average ?? []
  const cohortSize = q.data?.cohorts.reduce((s, c) => s + c.size, 0) ?? 0

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Cohort">
          <label className="field"><span>People who did</span>
            <input list="dl-events" value={state.start_event} onChange={(e) => set({ start_event: e.target.value })} aria-label="Start event" />
          </label>
          <label className="field"><span>and came back to do</span>
            <input list="dl-events" value={state.return_event} placeholder="same event" onChange={(e) => set({ return_event: e.target.value })} aria-label="Return event" />
          </label>
        </EditorSection>
        <EditorSection title="Periods" hint="Cohorts are formed from the first time a person did the start event in the selected date range.">
          <Segmented<'day' | 'week'> size="sm" label="Period" value={state.period}
            options={[{ id: 'day', label: 'Daily' }, { id: 'week', label: 'Weekly' }]}
            onChange={(p) => set(p === 'day'
              ? { period: 'day', periods: Math.min(state.periods * 7, 14) }
              : { period: 'week', periods: Math.max(1, Math.min(Math.round(state.periods / 7) || 6, 12)) })} />
          <label className="inline">Show
            <input type="number" className="narrow" min={1} max={30} value={state.periods}
              onChange={(e) => set({ periods: Math.max(1, Math.min(30, Number(e.target.value) || 1)) })} aria-label="Number of periods" />
            periods
          </label>
        </EditorSection>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="retention" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={q.data?.cohorts.every((c) => c.size === 0)}>
          <div className="stats">
            <StatTile label="People in cohorts" value={num(cohortSize)} hint={`${q.data?.cohorts.length ?? 0} cohorts`} />
            {[1, Math.min(4, avg.length - 1)].filter((k, i, a) => k > 0 && k < avg.length && a.indexOf(k) === i).map((k) => (
              <StatTile key={k} label={`${unit} ${k} retention`} value={avg[k] === null ? '—' : pct(avg[k]!)} hint="Average across cohorts" />
            ))}
          </div>
          <Card testId="retention-card" title="Cohort retention" meta={`${q.data?.timezone ?? ''} · ${ms(q.data?.took_ms ?? 0)}`}>
            {q.data && <RetentionTable data={q.data} />}
            <p className="footnote">Each cell is the share of the cohort that did the return event in that period; period 0 is the cohort's own period.</p>
          </Card>
        </Status>
      </section>
    </div>
  )
}
