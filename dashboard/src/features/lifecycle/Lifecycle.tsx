import type { Filter, Interval, LifecycleResponse } from '../../api/types'
import { readState, toDateRange, type Range } from '../../app/route'
import { api } from '../../api/client'
import { Legend } from '../../components/charts/common'
import { LIFECYCLE_STATUSES, LifecycleChart } from '../../components/charts/LifecycleChart'
import { completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Card, Segmented, StatTile, Status } from '../../components/ui'
import { bucketLabel, ms, num } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'

type Period = 'day' | 'week' | 'month'
export interface LifecycleState { event: string; period: Period; lookback_days: number; filters: Filter[] }

export const LIFECYCLE_DEFAULT: LifecycleState = { event: '', period: 'week', lookback_days: 90, filters: [] }
const LOOKBACKS = [30, 90, 180, 365]

export function lifecycleBody(state: LifecycleState, range: Range) {
  return {
    event: state.event.trim() || undefined,
    period: state.period,
    lookback_days: state.lookback_days,
    filters: completeFilters(state.filters),
    ...toDateRange(range),
  }
}

export const lifecycleEmpty = (d?: LifecycleResponse) => d?.periods.every((p) => p.new + p.returning + p.resurrecting + p.dormant === 0)

export function LifecycleLegend() {
  return <Legend items={LIFECYCLE_STATUSES.map((s) => ({ label: s.label, colorIndex: s.colorIndex }))} />
}

export function Lifecycle({ params, range, onChange, onInsight }: {
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
}) {
  const state = readState(params, LIFECYCLE_DEFAULT)
  const set = (patch: Partial<LifecycleState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(lifecycleBody(state, range)))
  const q = useQuery((signal) => api.lifecycle(JSON.parse(request), signal), [request])

  const d = q.data
  const sum = (k: 'new' | 'returning' | 'resurrecting' | 'dormant') => d?.periods.reduce((s, p) => s + p[k], 0) ?? 0
  const gained = sum('new') + sum('resurrecting')
  const lost = sum('dormant')
  const unit = state.period === 'day' ? 'days' : state.period === 'week' ? 'weeks' : 'months'

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Activity" hint="Leave empty to count any event as activity, or pick one, for example purchase, to follow customers.">
          <label className="field"><span>Active means did</span>
            <input list="dl-events" value={state.event} placeholder="any event" onChange={(e) => set({ event: e.target.value })} aria-label="Lifecycle event" />
          </label>
        </EditorSection>
        <EditorSection title="Period">
          <Segmented<Period> size="sm" label="Period" value={state.period}
            options={[{ id: 'day', label: 'Daily' }, { id: 'week', label: 'Weekly' }, { id: 'month', label: 'Monthly' }]}
            onChange={(period) => set({ period })} />
        </EditorSection>
        <EditorSection title="New means" hint={`A person is new in the period of their first activity after ${state.lookback_days} days without any. Longer look-backs scan more data.`}>
          <label className="inline">not seen in the last
            <select value={state.lookback_days} onChange={(e) => set({ lookback_days: Number(e.target.value) })} aria-label="Look-back days" className="op">
              {LOOKBACKS.map((l) => <option key={l} value={l}>{l} days</option>)}
            </select>
          </label>
        </EditorSection>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="lifecycle" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={lifecycleEmpty(d)}>
          {d && (
            <>
              <div className="stats">
                <StatTile label="New" value={num(sum('new'))} hint={`over ${d.periods.length} ${unit}`} swatch="var(--series-1)" testId="lifecycle-new" />
                <StatTile label="Resurrecting" value={num(sum('resurrecting'))} hint="came back after a gap" swatch="var(--series-3)" />
                <StatTile label="Dormant" value={num(lost)} hint="active, then inactive" swatch="var(--series-4)" />
                <StatTile label="Quick ratio" value={lost === 0 ? '—' : (gained / lost).toFixed(2)} hint="gained ÷ dormant" />
              </div>
              <Card testId="lifecycle-card" title={d.event ? `Lifecycle of ${d.event}` : 'Lifecycle of active persons'} meta={`${d.timezone} · ${ms(d.took_ms)}`}>
                <LifecycleLegend />
                <LifecycleChart data={d} />
                <p className="footnote">Returning: active in the previous period too. Resurrecting: active again after at least one inactive period. Dormant: active in the previous period but not in this one.</p>
              </Card>
              <Card title="By period">
                <div className="table-wrap">
                  <table className="lifecycle-table" data-testid="lifecycle-table">
                    <thead>
                      <tr>
                        <th>Period</th>
                        {LIFECYCLE_STATUSES.map((s) => <th key={s.id} className="num">{s.label}</th>)}
                        <th className="num">Active</th>
                      </tr>
                    </thead>
                    <tbody>
                      {[...d.periods].reverse().map((p) => (
                        <tr key={p.start}>
                          <td className="nowrap">{bucketLabel(p.start, d.period as Interval, true)}</td>
                          <td className="num">{num(p.new)}</td>
                          <td className="num">{num(p.returning)}</td>
                          <td className="num">{num(p.resurrecting)}</td>
                          <td className="num neg">{p.dormant ? `−${num(p.dormant)}` : '0'}</td>
                          <td className="num strong">{num(p.new + p.returning + p.resurrecting)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </Card>
            </>
          )}
        </Status>
      </section>
    </div>
  )
}
