import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Insight, InsightKind } from '../../api/types'
import type { Range } from '../../app/route'
import { Icon, type IconName } from '../../components/icons'
import { Badge, Card, FormMessage, IconButton, Status } from '../../components/ui'
import { dateTime, num } from '../../lib/format'
import { useQuery } from '../../lib/hooks'
import { InsightTile } from './InsightTile'
import { insightHref, KIND_LABEL } from './SaveInsight'

const KIND_ICON: Record<InsightKind, IconName> = { trends: 'trends', funnels: 'funnels', retention: 'retention', paths: 'paths', lifecycle: 'lifecycle', rfm: 'rfm' }

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
      setError(errorMessage(err))
    }
  }
  const remove = async (insight: Insight) => {
    if (!window.confirm(`Delete "${insight.name}"? It is also removed from every dashboard.`)) return
    try {
      await api.deleteInsight(insight.id)
      setVersion((v) => v + 1)
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <div className="stack">
      <Card title="Dashboards" meta={dashboards.data && `${num(dashboards.data.dashboards.length)} total`}
        actions={
          <form className="inline-create" onSubmit={create} data-testid="create-dashboard">
            <input required maxLength={200} placeholder="New dashboard name" value={name} onChange={(e) => setName(e.target.value)} aria-label="Dashboard name" />
            <button type="submit" className="primary"><Icon name="plus" />Create</button>
          </form>
        }>
        <Status state={dashboards} empty={dashboards.data?.dashboards.length === 0} emptyText="Create a dashboard, then add saved insights to it.">
          <div className="dashboard-cards" data-testid="dashboard-list">
            {dashboards.data?.dashboards.map((d) => (
              <a key={d.id} className="dashboard-card" href={`#/dashboards/${d.id}`} onClick={(e) => { e.preventDefault(); openDashboard(String(d.id)) }}>
                <span className="dashboard-card-icon"><Icon name="dashboards" size={18} /></span>
                <span className="dashboard-card-name">{d.name}</span>
                <span className="dashboard-card-meta">{num(d.items)} insight{d.items === 1 ? '' : 's'} · updated {dateTime(d.updated_at)}</span>
                <Icon name="chevron-right" className="icon-svg dashboard-card-arrow" />
              </a>
            ))}
          </div>
        </Status>
        <FormMessage error={error} />
      </Card>

      <Card title="Saved insights" meta="Save a trend, funnel, retention or paths query from its tab">
        <Status state={insights} empty={insights.data?.insights.length === 0} emptyText="Open an analysis tab and save it to see it here.">
          <div className="table-wrap">
            <table data-testid="insights-table">
              <thead><tr><th>Name</th><th>Type</th><th>Created by</th><th>Updated</th><th /></tr></thead>
              <tbody>
                {insights.data?.insights.map((i) => (
                  <tr key={i.id}>
                    <td><a className="insight-link" href={insightHref(i, params)}><Icon name={KIND_ICON[i.kind]} />{i.name}</a></td>
                    <td><Badge>{KIND_LABEL[i.kind]}</Badge></td>
                    <td className="muted">{i.created_by ?? '-'}</td>
                    <td className="nowrap tabular muted">{dateTime(i.updated_at)}</td>
                    <td className="num"><IconButton icon="trash" tone="danger" label={`Delete ${i.name}`} onClick={() => remove(i)} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Status>
      </Card>
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
      setError(errorMessage(err))
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
      <div className="dashboard-bar">
        <button type="button" className="back-link" onClick={onClose}><Icon name="chevron-left" />All dashboards</button>
        <div className="dashboard-heading">
          <h2 data-testid="dashboard-name">{d?.name ?? 'Dashboard'}</h2>
          <IconButton icon="edit" label="Rename dashboard" onClick={rename} disabled={!d} />
        </div>
        <span className="spacer" />
        <div className="select-wrap">
          <select value="" onChange={(e) => e.target.value && save({ insight_ids: [...ids, Number(e.target.value)] })}
            aria-label="Add insight" disabled={!d || available.length === 0} data-testid="add-insight">
            <option value="">{available.length ? 'Add insight…' : 'No more saved insights'}</option>
            {available.map((i) => <option key={i.id} value={i.id}>{i.name} ({KIND_LABEL[i.kind]})</option>)}
          </select>
          <Icon name="chevron-down" size={14} />
        </div>
        <IconButton icon="trash" tone="danger" label="Delete dashboard" onClick={remove} disabled={!d} />
      </div>
      {error && <div className="notice error" role="alert">{error}</div>}
      <Status state={dashboard} empty={d?.items.length === 0} emptyTitle="This dashboard is empty" emptyText="Add a saved insight from the menu above.">
        <div className="tiles" data-testid="dashboard-tiles">
            {d?.items.map((insight, i) => (
              <section className="card tile" key={insight.id} data-testid="dashboard-tile">
                <header className="card-head">
                  <a href={insightHref(insight, params)} className="tile-title">
                    <span className="tile-kind"><Icon name={KIND_ICON[insight.kind]} size={14} /></span>
                    {insight.name}
                  </a>
                  <span className="tile-actions">
                    <IconButton icon="chevron-left" label="Move left" onClick={() => move(i, -1)} disabled={i === 0} />
                    <IconButton icon="chevron-right" label="Move right" onClick={() => move(i, 1)} disabled={i === ids.length - 1} />
                    <IconButton icon="close" label="Remove from dashboard" onClick={() => save({ insight_ids: ids.filter((x) => x !== insight.id) })} />
                  </span>
                </header>
                <InsightTile insight={insight} range={range} />
              </section>
            ))}
          </div>
      </Status>
    </div>
  )
}
