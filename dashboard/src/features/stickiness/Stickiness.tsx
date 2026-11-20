import { api } from '../../api/client'
import type { Filter } from '../../api/types'
import { readState, toDateRange, type Range } from '../../app/route'
import { completeFilters, EditorSection, eventOrAction, FilterEditor, SuggestInput } from '../../components/editors'
import { Card, Segmented, StatTile, Status } from '../../components/ui'
import { stickinessCsv } from '../../lib/csv'
import { ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'

export type StickyInterval = 'day' | 'week'
export interface StickinessState { event: string; interval: StickyInterval; filters: Filter[] }
export const STICKINESS_DEFAULT: StickinessState = { event: '', interval: 'day', filters: [] }

export function stickinessBody(state: StickinessState, range: Range) {
  return { ...eventOrAction(state.event.trim()), interval: state.interval, filters: completeFilters(state.filters), ...toDateRange(range) }
}

export function stickinessRegulars(d: { persons: number; buckets: { periods: number; persons: number }[]; total_periods: number }): number {
  return d.buckets.filter((b) => b.periods * 2 >= d.total_periods).reduce((s, b) => s + b.persons, 0)
}

export function Stickiness({ params, range, onChange, onInsight }: {
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
}) {
  const state = readState(params, STICKINESS_DEFAULT)
  const set = (patch: Partial<StickinessState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(stickinessBody(state, range)))
  const q = useQuery((signal) => {
    const b = JSON.parse(request)
    return b.event || b.action ? api.stickiness(b, signal) : Promise.reject(new Error('Pick an event or an action'))
  }, [request])
  const d = q.data
  const unit = state.interval === 'day' ? 'day' : 'week'
  const max = Math.max(1, ...(d?.buckets.map((b) => b.persons) ?? [0]))
  const lastActive = d ? Math.max(1, ...d.buckets.filter((b) => b.persons > 0).map((b) => b.periods)) : 1
  const shown = d?.buckets.filter((b) => b.periods <= lastActive) ?? []
  const regulars = d ? stickinessRegulars(d) : 0

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Activity" hint="How many different days or weeks each person did this in the date range.">
          <label className="field" htmlFor="stickiness-event"><span>Event or action</span>
            <SuggestInput id="stickiness-event" list="events-actions" value={state.event} placeholder="event or action" onChange={(v) => set({ event: v })}
              aria-label="Stickiness event" />
          </label>
        </EditorSection>
        <EditorSection title="Count by">
          <Segmented<StickyInterval> size="sm" label="Interval" value={state.interval}
            options={[{ id: 'day', label: 'Days' }, { id: 'week', label: 'Weeks' }]} onChange={(interval) => set({ interval })} />
        </EditorSection>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="stickiness" query={state} params={params} onInsight={onInsight} csv={d && stickinessCsv(d)} />
        <Status state={q} empty={d?.persons === 0}>
          {d && (
            <>
              <div className="stats">
                <StatTile label="Persons" value={num(d.persons)} hint={`did ${d.event} at least once`} testId="stickiness-persons" />
                <StatTile label={`Average ${unit}s active`} value={d.average_periods === null ? '—' : String(d.average_periods)} hint={`out of ${d.total_periods}`} />
                <StatTile label="Regulars" value={d.persons ? pct(regulars / d.persons) : '—'} hint={`active in at least half of the ${unit}s`} />
              </div>
              <Card testId="stickiness-chart" title={`Persons by ${unit}s active`} meta={`${d.timezone} · ${ms(d.took_ms)}`}>
                <div className="sticky-bars">
                  {shown.map((b) => (
                    <div key={b.periods} className="sticky-row" data-periods={b.periods}>
                      <span className="sticky-label">{b.periods} {b.periods === 1 ? unit : `${unit}s`}</span>
                      <span className="sticky-track"><span className="sticky-fill" style={{ width: `${(b.persons / max) * 100}%` }} /></span>
                      <span className="sticky-value num">{num(b.persons)} <span className="muted small">{pct(b.share)}</span></span>
                    </div>
                  ))}
                </div>
                {lastActive < d.total_periods && <p className="footnote">Nobody did it on more than {lastActive} of the {d.total_periods} {unit}s.</p>}
                <p className="footnote">Each person counts once, in the row of how many different {unit}s they did it. A high tail means a habit; most people in the first row means one-off use.</p>
              </Card>
            </>
          )}
        </Status>
      </section>
    </div>
  )
}
