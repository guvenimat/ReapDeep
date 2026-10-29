import { api } from '../../api/client'
import type { Filter, PathsResponse } from '../../api/types'
import { readState, toDateRange, type Range } from '../../app/route'
import { completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Card, Segmented, Status } from '../../components/ui'
import { ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'

export interface PathsState { start_event: string; depth: number; window_seconds: number; pageview_paths: boolean; filters: Filter[] }

export const PATHS_DEFAULT: PathsState = { start_event: '$pageview', depth: 4, window_seconds: 86400, pageview_paths: true, filters: [] }

const WINDOWS = [{ id: 1800, label: '30m' }, { id: 3600, label: '1h' }, { id: 86400, label: '1d' }]
const DEPTHS = [2, 3, 4, 5, 6].map((d) => ({ id: d, label: String(d) }))

export function pathsBody(state: PathsState, range: Range) {
  return {
    start_event: state.start_event.trim(),
    depth: state.depth,
    window_seconds: state.window_seconds,
    pageview_paths: state.pageview_paths,
    filters: completeFilters(state.filters),
    ...toDateRange(range),
  }
}

function PathSteps({ data }: { data: PathsResponse }) {
  const starters = Math.max(1, data.persons)
  return (
    <div className="path-steps" data-testid="path-steps">
      {data.steps.map((s) => {
        const other = s.persons - s.nodes.reduce((a, n) => a + n.persons, 0)
        return (
          <div key={s.step} className="path-step">
            <div className="path-step-head"><span className="step-index-chip">{s.step}</span><span className="muted tabular">{num(s.persons)} persons</span></div>
            {s.nodes.map((n) => (
              <div key={n.name} className="path-node" title={`${n.name}: ${num(n.persons)} persons (${pct(n.persons / starters)} of starters)`}>
                <div className="path-node-label"><span className="truncate">{n.name}</span><span className="tabular muted">{pct(n.persons / starters)}</span></div>
                <div className="path-bar-track"><div className="path-bar" style={{ width: `${Math.max(2, (n.persons / starters) * 100)}%` }} /></div>
              </div>
            ))}
            {other > 0 && <div className="footnote">+ {num(other)} other</div>}
          </div>
        )
      })}
    </div>
  )
}

export function PathList({ data, limit }: { data: PathsResponse; limit?: number }) {
  const starters = Math.max(1, data.persons)
  return (
    <div className="table-wrap">
      <table data-testid="paths-table">
        <thead><tr><th>Path</th><th className="num">Persons</th><th className="num">Of starters</th></tr></thead>
        <tbody>
          {data.paths.slice(0, limit).map((p, i) => (
            <tr key={i}>
              <td>
                <span className="path-chips">
                  {p.nodes.map((n, k) => (
                    <span key={k} className="path-chip-wrap">{k > 0 && <Icon name="chevron-right" size={12} className="icon-svg path-arrow" />}<span className="chip">{n}</span></span>
                  ))}
                </span>
              </td>
              <td className="num strong">{num(p.persons)}</td>
              <td className="num">{pct(p.persons / starters)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function Paths({ params, range, onChange, onInsight }: {
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
}) {
  const state = readState(params, PATHS_DEFAULT)
  const set = (patch: Partial<PathsState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(pathsBody(state, range)))
  const q = useQuery((signal) => {
    const body = JSON.parse(request)
    return body.start_event ? api.paths(body, signal) : Promise.reject(new Error('Choose a start event'))
  }, [request])

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Start">
          <input list="dl-events" value={state.start_event} onChange={(e) => set({ start_event: e.target.value })} aria-label="Start event" />
          <label className="switch">
            <input type="checkbox" checked={state.pageview_paths} onChange={(e) => set({ pageview_paths: e.target.checked })} />
            <span className="switch-track" aria-hidden />
            Show page paths for $pageview
          </label>
        </EditorSection>
        <EditorSection title="Steps">
          <Segmented<number> size="sm" label="Depth" value={state.depth} options={DEPTHS} onChange={(depth) => set({ depth })} />
        </EditorSection>
        <EditorSection title="Within" hint="Each person's first start event in the range, then their next events within the window. Repeats in a row count once; $identify and $set are skipped.">
          <Segmented<number> size="sm" label="Window" value={state.window_seconds} options={WINDOWS} onChange={(v) => set({ window_seconds: v })} />
        </EditorSection>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="paths" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={q.data?.persons === 0}>
          <Card testId="paths-card" title={<>Paths from <span className="chip">{q.data?.start_event}</span></>}
            meta={`${num(q.data?.persons ?? 0)} persons · ${ms(q.data?.took_ms ?? 0)}`}>
            {q.data && <PathSteps data={q.data} />}
          </Card>
          <Card title="Most common paths">
            {q.data && <PathList data={q.data} />}
          </Card>
        </Status>
      </section>
    </div>
  )
}
