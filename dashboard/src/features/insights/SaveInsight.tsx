import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Insight, InsightKind } from '../../api/types'
import { Icon } from '../../components/icons'
import { useQuery } from '../../lib/hooks'

export const KIND_LABEL: Record<InsightKind, string> = { trends: 'Trend', funnels: 'Funnel', retention: 'Retention', paths: 'Paths', lifecycle: 'Lifecycle', rfm: 'RFM' }
const KIND_NOUN: Record<InsightKind, string> = {
  trends: 'trend', funnels: 'funnel', retention: 'retention analysis', paths: 'paths analysis', lifecycle: 'lifecycle analysis', rfm: 'RFM analysis',
}

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
      setMessage({ text: errorMessage(err), error: true })
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
    <div className="save-bar" data-testid="save-insight">
      <span className="save-icon"><Icon name="bookmark" /></span>
      {saved ? (
        <>
          <div className="save-title">
            <span className="eyebrow">Saved {KIND_NOUN[kind]}</span>
            <span className="strong" data-testid="insight-name">{saved.name}</span>
          </div>
          <button className="icon-button" onClick={rename} disabled={busy} aria-label="Rename" title="Rename"><Icon name="edit" /></button>
          {message && <span className={`form-message ${message.error ? 'error' : 'good'}`} role="status">{message.text}</span>}
          <span className="spacer" />
          {(dashboards.data?.dashboards.length ?? 0) > 0 && (
            <select value="" onChange={(e) => e.target.value && addToDashboard(Number(e.target.value))} aria-label="Add to dashboard" disabled={busy}>
              <option value="">Add to dashboard…</option>
              {dashboards.data?.dashboards.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          )}
          <button onClick={() => saveNew()} disabled={busy}>Save as new</button>
          <button className="primary" onClick={update} disabled={busy} data-testid="save-changes">Save changes</button>
          <button className="icon-button" onClick={() => onInsight(undefined)} aria-label="Start a new insight" title="New"><Icon name="plus" /></button>
        </>
      ) : (
        <form className="save-form" onSubmit={saveNew}>
          <input placeholder={`Name this ${KIND_NOUN[kind]} to save it`} value={name} onChange={(e) => setName(e.target.value)}
            aria-label="Insight name" maxLength={200} />
          {message && <span className={`form-message ${message.error ? 'error' : 'good'}`} role="status">{message.text}</span>}
          <button type="submit" className="primary" disabled={busy || !name.trim()} data-testid="save-insight-button">Save</button>
        </form>
      )}
    </div>
  )
}
