import { api, type Filter, type PathsResponse } from './api'
import { ms, num, pct } from './format'
import { SaveInsight } from './Insights'
import { readState, toDateRange, type rangeFromParams } from './route'
import { completeFilters, FilterEditor, Status, useDebounced, useQuery } from './ui'

type Range = ReturnType<typeof rangeFromParams>

export interface PathsState { start_event: string; depth: number; window_seconds: number; pageview_paths: boolean; filters: Filter[] }

export const PATHS_DEFAULT: PathsState = { start_event: '$pageview', depth: 4, window_seconds: 86400, pageview_paths: true, filters: [] }

const WINDOWS = [{ s: 1800, label: '30 min' }, { s: 3600, label: '1 hour' }, { s: 86400, label: '1 day' }]

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

export function PathSteps({ data }: { data: PathsResponse }) {
  const starters = Math.max(1, data.persons)
  return (
    <div className="path-steps" data-testid="path-steps">
      {data.steps.map((s) => (
        <div key={s.step} className="path-step">
          <div className="editor-title">Step {s.step} <span className="muted">· {num(s.persons)}</span></div>
          {s.nodes.map((n) => (
            <div key={n.name} className="path-node" title={`${n.name}: ${num(n.persons)} persons (${pct(n.persons / starters)} of starters)`}>
              <div className="path-node-label"><span className="truncate">{n.name}</span><span className="tabular muted">{num(n.persons)}</span></div>
              <div className="path-bar-track"><div className="path-bar" style={{ width: `${Math.max(2, (n.persons / starters) * 100)}%` }} /></div>
            </div>
          ))}
          {s.persons > s.nodes.reduce((a, n) => a + n.persons, 0) && (
            <div className="small muted">+ {num(s.persons - s.nodes.reduce((a, n) => a + n.persons, 0))} other</div>
          )}
        </div>
      ))}
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
              <td><span className="path-chips">{p.nodes.map((n, k) => <span key={k}>{k > 0 && <span className="muted"> → </span>}<span className="chip">{n}</span></span>)}</span></td>
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
  params: URLSearchParams
  range: Range
  onChange: (q: string) => void
  onInsight: (id?: string) => void
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
        <div className="editor-block">
          <div className="editor-title">Start</div>
          <input list="dl-events" value={state.start_event} onChange={(e) => set({ start_event: e.target.value })} aria-label="Start event" />
          <label className="inline check">
            <input type="checkbox" checked={state.pageview_paths} onChange={(e) => set({ pageview_paths: e.target.checked })} />
            Show page paths for $pageview
          </label>
        </div>
        <div className="editor-block">
          <div className="editor-title">Steps</div>
          <div className="segmented" role="group" aria-label="Depth">
            {[2, 3, 4, 5, 6].map((d) => <button key={d} className={state.depth === d ? 'active' : ''} onClick={() => set({ depth: d })}>{d}</button>)}
          </div>
        </div>
        <div className="editor-block">
          <div className="editor-title">Within</div>
          <div className="segmented" role="group" aria-label="Window">
            {WINDOWS.map((w) => <button key={w.s} className={state.window_seconds === w.s ? 'active' : ''} onClick={() => set({ window_seconds: w.s })}>{w.label}</button>)}
          </div>
          <p className="muted small">Each person's first start event in the range, then their next events within the window. Repeats in a row count once; $identify and $set are skipped.</p>
        </div>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="paths" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={q.data?.persons === 0}>
          <div className="card" data-testid="paths-card">
            <div className="card-head">
              <h2>Paths from {q.data?.start_event}</h2>
              <span className="muted">{num(q.data?.persons ?? 0)} persons · {ms(q.data?.took_ms ?? 0)}</span>
            </div>
            {q.data && <PathSteps data={q.data} />}
          </div>
          <div className="card">
            <div className="card-head"><h2>Most common paths</h2></div>
            {q.data && <PathList data={q.data} />}
          </div>
        </Status>
      </section>
    </div>
  )
}
