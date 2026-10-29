import { api } from '../../api/client'
import type { Breakdown, Filter, FunnelResponse } from '../../api/types'
import { readState, toDateRange, type Range } from '../../app/route'
import { Legend, MAX_SERIES, seriesColor } from '../../components/charts/common'
import { FunnelChart, type FunnelGroup } from '../../components/charts/FunnelChart'
import { AddButton, BreakdownEditor, completeBreakdown, completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Card, IconButton, Segmented, StatTile, Status } from '../../components/ui'
import { ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'

export interface FunnelState { steps: string[]; window_seconds: number; filters: Filter[]; breakdown?: Breakdown; sampling: number }

export const FUNNEL_DEFAULT: FunnelState = { steps: ['$pageview', '$identify'], window_seconds: 7 * 86400, filters: [], sampling: 1 }
const WINDOWS = [
  { id: 3600, label: '1h' }, { id: 86400, label: '1d' }, { id: 7 * 86400, label: '7d' }, { id: 30 * 86400, label: '30d' },
]
const SAMPLING = [
  { id: 1, label: 'Exact' }, { id: 0.25, label: '25%' }, { id: 0.1, label: '10%' },
]

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
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
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
  const overall = q.data?.results.length === 1 ? q.data.results[0].steps : undefined
  const last = overall?.[overall.length - 1]
  const worst = overall?.slice(1).reduce((w, s) => (s.conversion_from_previous < w.conversion_from_previous ? s : w), overall[1])

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Steps">
          {state.steps.map((s, i) => (
            <div className="row step-row" key={i}>
              <span className="step-no">{i + 1}</span>
              <input list="dl-events" placeholder="event" value={s} aria-label={`Step ${i + 1}`}
                onChange={(e) => set({ steps: state.steps.map((x, k) => (k === i ? e.target.value : x)) })} />
              {state.steps.length > 2 && (
                <IconButton icon="close" label="Remove step" onClick={() => set({ steps: state.steps.filter((_, k) => k !== i) })} />
              )}
            </div>
          ))}
          {state.steps.length < 8 && <AddButton onClick={() => set({ steps: [...state.steps, ''] })}>Add step</AddButton>}
        </EditorSection>
        <EditorSection title="Conversion window">
          <Segmented<number> size="sm" label="Conversion window" value={state.window_seconds} options={WINDOWS} onChange={(v) => set({ window_seconds: v })} />
        </EditorSection>
        <EditorSection title="Precision" hint="Sampling hashes persons and scales the counts back up; it trades exactness for speed on large projects.">
          <Segmented<number> size="sm" label="Sampling" value={state.sampling} options={SAMPLING} onChange={(v) => set({ sampling: v })} />
        </EditorSection>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
        <BreakdownEditor value={state.breakdown} onChange={(breakdown) => set({ breakdown })} allowCohort />
      </aside>

      <section className="content">
        <SaveInsight kind="funnels" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={groups.length === 0 || groups.every((g) => g.counts[0] === 0)}>
          {overall && last && (
            <div className="stats">
              <StatTile label="Entered" value={num(overall[0].count)} hint={overall[0].event} />
              <StatTile label="Converted" value={pct(last.conversion_from_first)} hint={`${num(last.count)} reached ${last.event}`} />
              {worst && <StatTile label="Biggest drop-off" value={pct(1 - worst.conversion_from_previous)} hint={`Before ${worst.event}`} />}
            </div>
          )}

          <Card testId="funnel-chart" title="Conversion"
            meta={<>{q.data && q.data.sampling < 1 && <span data-testid="funnel-sampled">Sampled: {pct(q.data.sampling)} of persons, counts scaled · </span>}{ms(q.data?.took_ms ?? 0)}</>}>
            <Legend items={groups.map((g) => ({ label: g.label, colorIndex: g.colorIndex }))} />
            <FunnelChart steps={stepNames} groups={groups} />
          </Card>

          <Card title="Step details">
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
                        <td className="nowrap"><span className="muted tabular">{s.step}.</span> {s.event}</td>
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
          </Card>
        </Status>
      </section>
    </div>
  )
}
