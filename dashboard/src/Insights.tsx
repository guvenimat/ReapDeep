import { useState, type FormEvent } from 'react'
import { api, type Insight, type InsightKind } from './api'
import { FunnelChart, Legend, TimeChart } from './charts'
import { dateTime, num } from './format'
import { FUNNEL_DEFAULT, funnelBody, funnelGroups, type FunnelState } from './Funnels'
import { PathList, PATHS_DEFAULT, pathsBody, type PathsState } from './Paths'
import { RETENTION_DEFAULT, retentionBody, RetentionTable, type RetentionState } from './Retention'
import type { rangeFromParams } from './route'
import { TREND_DEFAULT, trendBody, trendChartSeries, trendInterval, type TrendState } from './Trends'
import { Status, useQuery } from './ui'

type Range = ReturnType<typeof rangeFromParams>

const KIND_LABEL: Record<InsightKind, string> = { trends: 'Trend', funnels: 'Funnel', retention: 'Retention', paths: 'Paths' }

export function insightHref(insight: Insight, params: URLSearchParams) {
  const p = new URLSearchParams()
  for (const k of ['range', 'from', 'to']) {
    const v = params.get(k)
    if (v) p.set(k, v)
  }
  p.set('q', JSON.stringify(insight.query))
  p.set('insight', String(insight.id))
  return `#/${insight.kind}?${p.toString()}`
}

export function SaveInsight({ kind, query, params, onInsight }: {
  kind: InsightKind; query: object; params: URLSearchParams; onInsight: (id?: string) => void
}) {
  const insightId = Number(params.get('insight')) || undefined
  const [version, setVersion] = useState(0)
  const list = useQuery((signal) => api.insights(signal), [version])
  const dashboards = useQuery((signal) => api.dashboards(signal), [version])
  const saved = list.data?.insights.find((i) => i.id === insightId && i.kind === kind)
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ text: string; error?: boolean }>()

  const run = async (fn: () => Promise<string>) => {
    setBusy(true)
    setMessage(undefined)
    try {
      setMessage({ text: await fn() })
      setVersion((v) => v + 1)
    } catch (err) {
      setMessage({ text: err instanceof Error ? err.message : String(err), error: true })
    } finally {
      setBusy(false)
    }
  }

  const saveNew = (e?: FormEvent) => {
    e?.preventDefault()
    const n = (saved ? `${saved.name} (copy)` : name).trim()
    if (!n) return
    void run(async () => {
      const created = await api.createInsight({ name: n, kind, query })
      setName('')
      onInsight(String(created.id))
      return `Saved as "${created.name}"`
    })
  }
  const update = () => saved && run(async () => {
    await api.updateInsight(saved.id, { query })
    return 'Changes saved'
  })
  const rename = () => {
    if (!saved) return
    const n = window.prompt('Insight name', saved.name)?.trim()
    if (n && n !== saved.name) void run(async () => {
      await api.updateInsight(saved.id, { name: n })
      return 'Renamed'
    })
  }
  const addToDashboard = (dashboardId: number) => saved && run(async () => {
    const d = await api.dashboard(dashboardId)
    if (d.items.some((i) => i.id === saved.id)) return `Already on "${d.name}"`
    await api.updateDashboard(dashboardId, { insight_ids: [...d.items.map((i) => i.id), saved.id] })
    return `Added to "${d.name}"`
  })

  return (
    <div className="card save-bar" data-testid="save-insight">
      {saved ? (
        <>
          <span className="small muted">Saved insight</span>
          <span className="strong" data-testid="insight-name">{saved.name}</span>
          <button className="link" onClick={rename} disabled={busy}>Rename</button>
          <span className="spacer" />
          <button className="primary" onClick={update} disabled={busy} data-testid="save-changes">Save changes</button>
          <button onClick={() => saveNew()} disabled={busy}>Save as new</button>
          {(dashboards.data?.dashboards.length ?? 0) > 0 && (
            <select value="" onChange={(e) => e.target.value && addToDashboard(Number(e.target.value))} aria-label="Add to dashboard" disabled={busy}>
              <option value="">Add to dashboard...</option>
              {dashboards.data?.dashboards.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          )}
          <button className="link" onClick={() => onInsight(undefined)}>New</button>
        </>
      ) : (
        <form className="row grow-row" onSubmit={saveNew}>
          <input placeholder={`Name this ${KIND_LABEL[kind].toLowerCase()} to save it`} value={name} onChange={(e) => setName(e.target.value)}
            aria-label="Insight name" maxLength={200} className="grow" />
          <button type="submit" className="primary" disabled={busy || !name.trim()} data-testid="save-insight-button">Save</button>
        </form>
      )}
      {message && <span className={`small ${message.error ? 'login-error' : 'good'}`} role="status">{message.text}</span>}
    </div>
  )
}

export function Dashboards({ id, params, range, openDashboard }: {
  id?: string; params: URLSearchParams; range: Range; openDashboard: (id?: string) => void
}) {
  return id
    ? <DashboardView id={Number(id)} params={params} range={range} onClose={() => openDashboard(undefined)} />
    : <DashboardList params={params} openDashboard={openDashboard} />
}

function DashboardList({ params, openDashboard }: { params: URLSearchParams; openDashboard: (id?: string) => void }) {
  const [version, setVersion] = useState(0)
  const dashboards = useQuery((signal) => api.dashboards(signal), [version])
  const insights = useQuery((signal) => api.insights(signal), [version])
  const [name, setName] = useState('')
  const [error, setError] = useState<string>()

  const create = async (e: FormEvent) => {
    e.preventDefault()
    setError(undefined)
    try {
      const d = await api.createDashboard({ name })
      setName('')
      openDashboard(String(d.id))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }
  const remove = async (insight: Insight) => {
    if (!window.confirm(`Delete "${insight.name}"? It is also removed from every dashboard.`)) return
    try {
      await api.deleteInsight(insight.id)
      setVersion((v) => v + 1)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  return (
    <div className="stack">
      <div className="card">
        <div className="card-head"><h2>Dashboards</h2></div>
        <Status state={dashboards} empty={dashboards.data?.dashboards.length === 0}>
          <div className="dashboard-cards" data-testid="dashboard-list">
            {dashboards.data?.dashboards.map((d) => (
              <a key={d.id} className="dashboard-card" href={`#/dashboards/${d.id}`} onClick={(e) => { e.preventDefault(); openDashboard(String(d.id)) }}>
                <span className="strong">{d.name}</span>
                <span className="small muted">{num(d.items)} insight{d.items === 1 ? '' : 's'} · updated {dateTime(d.updated_at)}</span>
              </a>
            ))}
          </div>
        </Status>
        <form className="row settings-inline" onSubmit={create} data-testid="create-dashboard">
          <input required maxLength={200} placeholder="New dashboard name" value={name} onChange={(e) => setName(e.target.value)} aria-label="Dashboard name" className="grow" />
          <button type="submit" className="primary">Create dashboard</button>
        </form>
        {error && <div className="login-error" role="alert">{error}</div>}
      </div>

      <div className="card">
        <div className="card-head"><h2>Saved insights</h2><span className="muted small">Save a trend or funnel from its tab</span></div>
        <Status state={insights} empty={insights.data?.insights.length === 0}>
          <div className="table-wrap">
            <table data-testid="insights-table">
              <thead><tr><th>Name</th><th>Type</th><th>Created by</th><th>Updated</th><th /></tr></thead>
              <tbody>
                {insights.data?.insights.map((i) => (
                  <tr key={i.id}>
                    <td><a href={insightHref(i, params)}>{i.name}</a></td>
                    <td>{KIND_LABEL[i.kind]}</td>
                    <td className="muted">{i.created_by ?? '-'}</td>
                    <td className="nowrap tabular">{dateTime(i.updated_at)}</td>
                    <td className="num"><button className="link danger-link" onClick={() => remove(i)}>Delete</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Status>
      </div>
    </div>
  )
}

function DashboardView({ id, params, range, onClose }: { id: number; params: URLSearchParams; range: Range; onClose: () => void }) {
  const [version, setVersion] = useState(0)
  const dashboard = useQuery((signal) => api.dashboard(id, signal), [id, version])
  const insights = useQuery((signal) => api.insights(signal), [])
  const [error, setError] = useState<string>()
  const d = dashboard.data
  const ids = d?.items.map((i) => i.id) ?? []
  const available = insights.data?.insights.filter((i) => !ids.includes(i.id)) ?? []

  const save = async (body: { name?: string; insight_ids?: number[] }) => {
    setError(undefined)
    try {
      await api.updateDashboard(id, body)
      setVersion((v) => v + 1)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }
  const move = (index: number, delta: number) => {
    const next = [...ids]
    const [item] = next.splice(index, 1)
    next.splice(index + delta, 0, item)
    void save({ insight_ids: next })
  }
  const rename = () => {
    const n = d && window.prompt('Dashboard name', d.name)?.trim()
    if (n && n !== d.name) void save({ name: n })
  }
  const remove = async () => {
    if (!d || !window.confirm(`Delete dashboard "${d.name}"? Its insights are kept.`)) return
    await api.deleteDashboard(id)
    onClose()
  }

  return (
    <div className="stack">
      <div className="card save-bar">
        <button className="link" onClick={onClose}>All dashboards</button>
        <h2 data-testid="dashboard-name">{d?.name ?? 'Dashboard'}</h2>
        <button className="link" onClick={rename} disabled={!d}>Rename</button>
        <span className="spacer" />
        <select value="" onChange={(e) => e.target.value && save({ insight_ids: [...ids, Number(e.target.value)] })}
          aria-label="Add insight" disabled={!d || available.length === 0} data-testid="add-insight">
          <option value="">{available.length ? 'Add insight...' : 'No more saved insights'}</option>
          {available.map((i) => <option key={i.id} value={i.id}>{i.name} ({KIND_LABEL[i.kind]})</option>)}
        </select>
        <button className="link danger-link" onClick={remove} disabled={!d}>Delete dashboard</button>
      </div>
      {error && <div className="notice error" role="alert">{error}</div>}
      <Status state={dashboard} empty={d?.items.length === 0}>
        <div className="tiles" data-testid="dashboard-tiles">
          {d?.items.map((insight, i) => (
            <div className="card tile" key={insight.id} data-testid="dashboard-tile">
              <div className="card-head">
                <a href={insightHref(insight, params)} className="strong">{insight.name}</a>
                <span className="tile-actions">
                  <button className="icon" onClick={() => move(i, -1)} disabled={i === 0} aria-label="Move left">‹</button>
                  <button className="icon" onClick={() => move(i, 1)} disabled={i === ids.length - 1} aria-label="Move right">›</button>
                  <button className="icon" onClick={() => save({ insight_ids: ids.filter((x) => x !== insight.id) })} aria-label="Remove from dashboard">×</button>
                </span>
              </div>
              <InsightTile insight={insight} range={range} />
            </div>
          ))}
        </div>
      </Status>
    </div>
  )
}

function InsightTile({ insight, range }: { insight: Insight; range: Range }) {
  if (insight.kind === 'trends') return <TrendTile state={{ ...TREND_DEFAULT, ...(insight.query as Partial<TrendState>) }} range={range} />
  if (insight.kind === 'retention') return <RetentionTile state={{ ...RETENTION_DEFAULT, ...(insight.query as Partial<RetentionState>) }} range={range} />
  if (insight.kind === 'paths') return <PathsTile state={{ ...PATHS_DEFAULT, ...(insight.query as Partial<PathsState>) }} range={range} />
  return <FunnelTile state={{ ...FUNNEL_DEFAULT, ...(insight.query as Partial<FunnelState>) }} range={range} />
}

function TrendTile({ state, range }: { state: TrendState; range: Range }) {
  const body = JSON.stringify(trendBody(state, range))
  const q = useQuery((signal) => api.trends(JSON.parse(body), signal), [body])
  const series = trendChartSeries(q.data)
  const buckets = q.data?.results[0]?.data.map((d) => d.bucket) ?? []
  const total = q.data?.results.length === 1 ? q.data.results[0].total : undefined
  return (
    <Status state={q} empty={(q.data?.results ?? []).every((r) => r.total === 0)}>
      {total !== undefined && <div className="tile-total" data-testid="tile-total">{num(total)}</div>}
      <Legend items={series.map((s) => ({ label: s.label, colorIndex: s.colorIndex }))} />
      <TimeChart buckets={buckets} interval={trendInterval(state, range)} series={series} mode={state.chart} />
    </Status>
  )
}

function FunnelTile({ state, range }: { state: FunnelState; range: Range }) {
  const body = JSON.stringify(funnelBody(state, range))
  const q = useQuery((signal) => api.funnels(JSON.parse(body), signal), [body])
  const groups = funnelGroups(q.data)
  return (
    <Status state={q} empty={groups.every((g) => g.counts[0] === 0)}>
      <Legend items={groups.map((g) => ({ label: g.label, colorIndex: g.colorIndex }))} />
      <FunnelChart steps={q.data?.results[0]?.steps.map((s) => s.event) ?? []} groups={groups} />
    </Status>
  )
}

function RetentionTile({ state, range }: { state: RetentionState; range: Range }) {
  const body = JSON.stringify(retentionBody(state, range))
  const q = useQuery((signal) => api.retention(JSON.parse(body), signal), [body])
  return (
    <Status state={q} empty={q.data?.cohorts.every((c) => c.size === 0)}>
      {q.data && <RetentionTable data={q.data} maxCohorts={5} />}
    </Status>
  )
}

function PathsTile({ state, range }: { state: PathsState; range: Range }) {
  const body = JSON.stringify(pathsBody(state, range))
  const q = useQuery((signal) => api.paths(JSON.parse(body), signal), [body])
  return (
    <Status state={q} empty={q.data?.persons === 0}>
      {q.data && <PathList data={q.data} limit={6} />}
    </Status>
  )
}
