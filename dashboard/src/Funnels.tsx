import { api, type Breakdown, type Filter, type FunnelResponse } from './api'
import { FunnelChart, Legend, MAX_SERIES, seriesColor, type FunnelGroup } from './charts'
import { ms, num, pct } from './format'
import { readState, toDateRange, type rangeFromParams } from './route'
import { SaveInsight } from './Insights'
import { BreakdownEditor, completeBreakdown, completeFilters, FilterEditor, Status, useDebounced, useQuery } from './ui'

export interface FunnelState { steps: string[]; window_seconds: number; filters: Filter[]; breakdown?: Breakdown; sampling: number }

export const FUNNEL_DEFAULT: FunnelState = { steps: ['$pageview', '$identify'], window_seconds: 7 * 86400, filters: [], sampling: 1 }
const WINDOWS = [
  { s: 3600, label: '1 hour' }, { s: 86400, label: '1 day' }, { s: 7 * 86400, label: '7 days' }, { s: 30 * 86400, label: '30 days' },
]
const SAMPLING = [
  { v: 1, label: 'Exact' }, { v: 0.25, label: 'Fast 25%' }, { v: 0.1, label: 'Fast 10%' },
]

type Range = ReturnType<typeof rangeFromParams>

export function funnelBody(state: FunnelState, range: Range) {
  return {
    steps: state.steps.filter((s) => s.trim()).map((event) => ({ event })),
    window_seconds: state.window_seconds,
    filters: completeFilters(state.filters),
    breakdown: completeBreakdown(state.breakdown),
    sampling: state.sampling,
    ...toDateRange(range),
  }
}

export function funnelGroups(data?: FunnelResponse): FunnelGroup[] {
  return (data?.results ?? []).slice(0, MAX_SERIES).map((r, i) => ({
    label: r.breakdown_value ?? 'All', colorIndex: i, counts: r.steps.map((s) => s.count),
  }))
}

export function Funnels({ params, range, onChange, onInsight }: {
  params: URLSearchParams
  range: ReturnType<typeof rangeFromParams>
  onChange: (q: string) => void
  onInsight: (id?: string) => void
}) {
  const state = readState(params, FUNNEL_DEFAULT)
  const set = (patch: Partial<FunnelState>) => onChange(JSON.stringify({ ...state, ...patch }))

  const request = useDebounced(JSON.stringify(funnelBody(state, range)))
  const q = useQuery((signal) => {
    const body = JSON.parse(request)
    return body.steps.length >= 2 ? api.funnels(body, signal) : Promise.reject(new Error('Enter at least 2 steps'))
  }, [request])

  const results = (q.data?.results ?? []).slice(0, MAX_SERIES)
  const groups = funnelGroups(q.data)
  const stepNames = q.data?.results[0]?.steps.map((s) => s.event) ?? []

  return (
    <div className="layout">
      <aside className="panel">
        <div className="editor-block">
          <div className="editor-title">Steps</div>
          {state.steps.map((s, i) => (
            <div className="row" key={i}>
              <span className="step-no">{i + 1}</span>
              <input list="dl-events" placeholder="event" value={s} aria-label={`Step ${i + 1}`}
                onChange={(e) => set({ steps: state.steps.map((x, k) => (k === i ? e.target.value : x)) })} />
              {state.steps.length > 2 && (
                <button className="icon" aria-label="Remove step" onClick={() => set({ steps: state.steps.filter((_, k) => k !== i) })}>×</button>
              )}
            </div>
          ))}
          {state.steps.length < 8 && <button className="link" onClick={() => set({ steps: [...state.steps, ''] })}>+ Add step</button>}
        </div>
        <div className="editor-block">
          <div className="editor-title">Conversion window</div>
          <div className="segmented" role="group" aria-label="Conversion window">
            {WINDOWS.map((w) => (
              <button key={w.s} className={state.window_seconds === w.s ? 'active' : ''} onClick={() => set({ window_seconds: w.s })}>{w.label}</button>
            ))}
          </div>
        </div>
        <div className="editor-block">
          <div className="editor-title">Precision</div>
          <div className="segmented" role="group" aria-label="Sampling">
            {SAMPLING.map((x) => (
              <button key={x.v} className={state.sampling === x.v ? 'active' : ''} onClick={() => set({ sampling: x.v })}>{x.label}</button>
            ))}
          </div>
        </div>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
        <BreakdownEditor value={state.breakdown} onChange={(breakdown) => set({ breakdown })} />
      </aside>

      <section className="content">
        <SaveInsight kind="funnels" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={groups.length === 0 || groups.every((g) => g.counts[0] === 0)}>
          <div className="card" data-testid="funnel-chart">
            <div className="card-head">
              <h2>Funnel</h2>
              <span className="muted">{q.data && q.data.sampling < 1 && <span data-testid="funnel-sampled">Sampled: {pct(q.data.sampling)} of persons, counts scaled · </span>}{ms(q.data?.took_ms ?? 0)}</span>
            </div>
            <Legend items={groups.map((g) => ({ label: g.label, colorIndex: g.colorIndex }))} />
            <FunnelChart steps={stepNames} groups={groups} />
          </div>

          <div className="card">
            <div className="table-wrap">
              <table data-testid="funnel-table">
                <thead>
                  <tr>
                    {groups.length > 1 && <th>Breakdown</th>}
                    <th>Step</th>
                    <th className="num">Persons</th>
                    <th className="num">Of first step</th>
                    <th className="num">Of previous step</th>
                    <th className="num">Drop-off</th>
                  </tr>
                </thead>
                <tbody>
                  {results.flatMap((r, gi) =>
                    r.steps.map((s) => (
                      <tr key={`${gi}-${s.step}`}>
                        {groups.length > 1 && (
                          <td className="nowrap">{s.step === 1 && <><span className="swatch" style={{ background: seriesColor(gi) }} />{groups[gi].label}</>}</td>
                        )}
                        <td className="nowrap">{s.step}. {s.event}</td>
                        <td className="num strong">{num(s.count)}</td>
                        <td className="num">{pct(s.conversion_from_first)}</td>
                        <td className="num">{s.step === 1 ? '—' : pct(s.conversion_from_previous)}</td>
                        <td className="num">{s.step === 1 ? '—' : pct(1 - s.conversion_from_previous)}</td>
                      </tr>
                    )),
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </Status>
      </section>
    </div>
  )
}
