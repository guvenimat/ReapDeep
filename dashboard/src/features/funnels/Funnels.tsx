import { useState } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Breakdown, Filter, FunnelResponse } from '../../api/types'
import { readState, toDateRange, type Range } from '../../app/route'
import { Legend, MAX_SERIES, seriesColor } from '../../components/charts/common'
import { FunnelChart, type FunnelGroup } from '../../components/charts/FunnelChart'
import { AddButton, BreakdownEditor, completeBreakdown, completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Card, FormMessage, IconButton, Segmented, StatTile, Status } from '../../components/ui'
import { duration, ms, num, pct, shortId } from '../../lib/format'
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
  const times = q.data?.times
  const timeOf = (step: number) => times?.steps.find((t) => t.step === step)
  const [dropped, setDropped] = useState<number>()
  const body = JSON.parse(request)

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
              {times?.median_seconds != null && (
                <StatTile label="Median time to convert" value={duration(times.median_seconds)} testId="funnel-median-time"
                  hint={`average ${duration(times.avg_seconds ?? 0)}, first to last step`} />
              )}
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
                    <th className="num" title="Median time from the previous step, for persons who converted">Median time</th>
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
                        <td className="num">
                          {s.step === 1 ? '—' : groups.length === 1 && r.steps[s.step - 2].count > s.count ? (
                            <button type="button" className={`link dropoff${dropped === s.step - 1 ? ' active' : ''}`} data-step={s.step - 1}
                              title={`${num(r.steps[s.step - 2].count - s.count)} persons dropped before ${s.event}; show them`}
                              onClick={() => setDropped(dropped === s.step - 1 ? undefined : s.step - 1)}>
                              {pct(1 - s.conversion_from_previous)}
                            </button>
                          ) : pct(1 - s.conversion_from_previous)}
                        </td>
                        <td className="num muted">{s.step === 1 || timeOf(s.step)?.median_seconds == null ? '—' : duration(timeOf(s.step)!.median_seconds!)}</td>
                      </tr>
                    )),
                  )}
                </tbody>
              </table>
            </div>
            {times && <p className="footnote">Click a drop-off to list the persons who left at that step. Times follow each person from their first {stepNames[0]} to the earliest next step inside the window, so they can cover slightly fewer persons than the counts.</p>}
          </Card>

          {dropped !== undefined && overall && groups.length === 1 && (
            <DroppedPersons key={`${request}|${dropped}`} body={body} step={dropped} from={overall[dropped - 1].event} to={overall[dropped].event}
              onClose={() => setDropped(undefined)} />
          )}
        </Status>
      </section>
    </div>
  )
}

function DroppedPersons({ body, step, from, to, onClose }: {
  body: ReturnType<typeof funnelBody>; step: number; from: string; to: string; onClose: () => void
}) {
  const request = { steps: body.steps, window_seconds: body.window_seconds, filters: body.filters, date_from: body.date_from, date_to: body.date_to, step, kind: 'dropped' }
  const q = useQuery((signal) => api.funnelPersons({ ...request, limit: 100 }, signal), [JSON.stringify(request)])
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ text: string; id?: number; error?: boolean }>()

  const saveCohort = async () => {
    setBusy(true)
    setMessage(undefined)
    try {
      const all = await api.funnelPersons({ ...request, limit: 10000 })
      const cohort = await api.createCohort({
        name: `Dropped after ${from} (${new Date().toISOString().slice(0, 10)})`,
        description: `Reached ${from} but not ${to} in the funnel ${body.steps.map((s) => s.event).join(' > ')}`,
        kind: 'static',
        members: all.persons.map((p) => p.id),
      })
      setMessage({ text: `Saved ${num(all.persons.length)} persons as "${cohort.name}"`, id: cohort.id })
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card testId="funnel-dropped" title={`Dropped after ${from}`}
      meta={q.data ? `${num(q.data.total)} persons reached step ${step} but not ${to}` : undefined}
      actions={<>
        <button type="button" className="small" onClick={saveCohort} disabled={busy || !q.data?.total} data-testid="save-dropped-cohort">
          {busy ? 'Saving…' : 'Save as cohort'}
        </button>
        <IconButton icon="close" label="Close" onClick={onClose} />
      </>}>
      <FormMessage error={message?.error ? message.text : undefined} />
      {message && !message.error && <div className="form-message good" role="status">{message.text} · <a href={`#/cohorts/${message.id}`}>open</a></div>}
      <Status state={q} empty={q.data?.total === 0} emptyTitle="Nobody dropped here">
        <div className="table-wrap">
          <table>
            <thead><tr><th>Person</th><th>Distinct ids</th></tr></thead>
            <tbody>
              {q.data?.persons.map((p) => (
                <tr key={p.id}>
                  <td><a href={`#/persons/${encodeURIComponent(p.id)}`}>{String(p.properties.email ?? p.properties.name ?? shortId(p.id))}</a>
                    {!p.is_identified && <span className="cell-sub">anonymous</span>}</td>
                  <td className="muted">{p.distinct_ids.length}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {q.data && q.data.total > q.data.persons.length && <p className="footnote">Showing the first {q.data.persons.length}; the cohort takes up to 10,000. A saved cohort is a fixed list.</p>}
      </Status>
    </Card>
  )
}
