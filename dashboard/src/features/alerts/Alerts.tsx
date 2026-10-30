import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Alert, AlertCondition, AlertWindow } from '../../api/types'
import { Badge, Card, FormMessage, IconButton, Status } from '../../components/ui'
import { ago, dateTime, num } from '../../lib/format'
import { useQuery } from '../../lib/hooks'

const CONDITIONS: { id: AlertCondition; label: string; unit: string }[] = [
  { id: 'above', label: 'is above', unit: '' },
  { id: 'below', label: 'is below', unit: '' },
  { id: 'increase', label: 'grows by at least', unit: '%' },
  { id: 'decrease', label: 'drops by at least', unit: '%' },
]
const WINDOWS: { id: AlertWindow; label: string }[] = [
  { id: 'hour', label: 'last hour' }, { id: 'day', label: 'last day' }, { id: 'week', label: 'last week' },
]

export function describeAlert(a: Pick<Alert, 'condition' | 'threshold' | 'window'>) {
  const c = CONDITIONS.find((x) => x.id === a.condition)!
  const span = WINDOWS.find((w) => w.id === a.window)!.label
  return c.unit ? `${span} ${c.label} ${num(a.threshold)}% vs the window before` : `${span} ${c.label} ${num(a.threshold)}`
}

const STATE: Record<Alert['state'], { label: string; tone: 'good' | 'danger' | 'muted' | 'accent' }> = {
  pending: { label: 'Not checked yet', tone: 'muted' },
  ok: { label: 'OK', tone: 'good' },
  firing: { label: 'Firing', tone: 'danger' },
  error: { label: 'Error', tone: 'danger' },
}

export function Alerts({ canManage }: { canManage: boolean }) {
  const [version, setVersion] = useState(0)
  const [editing, setEditing] = useState<Alert | 'new'>()
  const [selected, setSelected] = useState<number>()
  const [secret, setSecret] = useState<{ name: string; value: string }>()
  const [message, setMessage] = useState<{ text: string; error?: boolean }>()
  const list = useQuery((signal) => api.alerts(signal), [version])
  const refresh = () => setVersion((v) => v + 1)

  const run = async (fn: () => Promise<string>) => {
    setMessage(undefined)
    try {
      setMessage({ text: await fn() })
      refresh()
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    }
  }

  const alerts = list.data?.alerts ?? []
  const current = alerts.find((a) => a.id === selected)

  return (
    <div className="stack">
      {secret && (
        <div className="notice warn" role="status" data-testid="alert-secret">
          <span>Signing secret of "{secret.name}": <code className="chip">{secret.value}</code> · verify the
            <code className="chip">X-ReapDeep-Signature</code> header as <code className="chip">sha256=HMAC-SHA256(secret, body)</code>.</span>
          <IconButton icon="close" label="Hide secret" onClick={() => setSecret(undefined)} />
        </div>
      )}
      {editing && (
        <AlertEditor alert={editing === 'new' ? undefined : editing} onCancel={() => setEditing(undefined)}
          onSaved={(a, created) => {
            setEditing(undefined)
            if (created && a.secret) setSecret({ name: a.name, value: a.secret })
            refresh()
          }} />
      )}
      <Card testId="alerts-card" title="Alerts" meta="Checked automatically; a webhook is called when an alert starts or stops firing"
        actions={canManage && !editing ? <button type="button" className="primary" onClick={() => setEditing('new')} data-testid="new-alert">New alert</button> : undefined}>
        <FormMessage error={message?.error ? message.text : undefined} success={message?.error ? undefined : message?.text} />
        <Status state={list} empty={alerts.length === 0} emptyTitle="No alerts yet"
          emptyText="Save a trend insight, then create an alert to be notified when it crosses a threshold or changes sharply.">
          <div className="table-wrap">
            <table data-testid="alerts-table">
              <thead>
                <tr><th>Alert</th><th>State</th><th className="num">Last value</th><th>Checked</th>{canManage && <th />}</tr>
              </thead>
              <tbody>
                {alerts.map((a) => (
                  <tr key={a.id} className={`clickable${selected === a.id ? ' active' : ''}`} onClick={() => setSelected(selected === a.id ? undefined : a.id)}>
                    <td>
                      <span className="strong">{a.name}</span>{!a.enabled && <span className="muted small"> · paused</span>}
                      <span className="cell-sub">{a.insight_name}: {describeAlert(a)}</span>
                    </td>
                    <td><Badge tone={STATE[a.state].tone}>{STATE[a.state].label}</Badge></td>
                    <td className="num">{a.last_value === null ? '—' : num(a.last_value)}</td>
                    <td className="nowrap muted" title={a.last_error ?? undefined}>{a.last_checked_at ? ago(a.last_checked_at) : '—'}</td>
                    {canManage && (
                      <td className="actions" onClick={(e) => e.stopPropagation()}>
                        <IconButton icon="refresh" label="Check now" onClick={() => run(async () => {
                          const r = await api.checkAlert(a.id)
                          setSelected(a.id)
                          return `${a.name}: ${STATE[r.state].label.toLowerCase()} · ${r.message}`
                        })} />
                        <IconButton icon="edit" label="Edit alert" onClick={() => setEditing(a)} />
                        <IconButton icon="trash" label="Delete alert" tone="danger" onClick={() => {
                          if (window.confirm(`Delete the alert "${a.name}"?`)) void run(async () => { await api.deleteAlert(a.id); return `Deleted "${a.name}"` })
                        }} />
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Status>
      </Card>
      {current && <AlertHistory key={`${current.id}-${version}`} alert={current} />}
    </div>
  )
}

function AlertHistory({ alert }: { alert: Alert }) {
  const events = useQuery((signal) => api.alertEvents(alert.id, signal), [alert.id])
  return (
    <Card testId="alert-history" title={`History of ${alert.name}`} meta={alert.webhook_url}>
      <Status state={events} empty={events.data?.events.length === 0} emptyTitle="Nothing happened yet" emptyText="Events appear when the alert fires, resolves or fails.">
        <div className="table-wrap">
          <table>
            <thead><tr><th>When</th><th>Event</th><th>Details</th><th>Webhook</th></tr></thead>
            <tbody>
              {events.data?.events.map((e) => (
                <tr key={e.id}>
                  <td className="nowrap muted">{dateTime(e.created_at)}</td>
                  <td><Badge tone={e.kind === 'resolved' ? 'good' : 'danger'}>{e.kind}</Badge></td>
                  <td>{e.message}</td>
                  <td className="nowrap">{e.kind === 'error' ? '—' : e.response_status ? `HTTP ${e.response_status}` : <span className="form-message error">{e.delivery_error}</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
    </Card>
  )
}

function AlertEditor({ alert, onSaved, onCancel }: { alert?: Alert; onSaved: (a: Alert, created: boolean) => void; onCancel: () => void }) {
  const insights = useQuery((signal) => api.insights(signal), [])
  const trends = (insights.data?.insights ?? []).filter((i) => i.kind === 'trends')
  const [name, setName] = useState(alert?.name ?? '')
  const [insightId, setInsightId] = useState<number | ''>(alert?.insight_id ?? '')
  const [condition, setCondition] = useState<AlertCondition>(alert?.condition ?? 'above')
  const [threshold, setThreshold] = useState(String(alert?.threshold ?? ''))
  const [span, setSpan] = useState<AlertWindow>(alert?.window ?? 'day')
  const [url, setUrl] = useState(alert?.webhook_url ?? '')
  const [enabled, setEnabled] = useState(alert?.enabled ?? true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const unit = CONDITIONS.find((c) => c.id === condition)!.unit

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      const body = { name: name.trim(), insight_id: Number(insightId), condition, threshold: Number(threshold), window: span, webhook_url: url.trim(), enabled }
      onSaved(alert ? await api.updateAlert(alert.id, body) : await api.createAlert(body), !alert)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} data-testid="alert-editor">
      <Card title={alert ? `Edit ${alert.name}` : 'New alert'} actions={<>
        <button type="button" onClick={onCancel}>Cancel</button>
        <button type="submit" className="primary" disabled={busy || !name.trim() || insightId === '' || threshold === '' || !url.trim()}>
          {busy ? 'Saving…' : alert ? 'Save changes' : 'Create alert'}
        </button>
      </>}>
        <div className="field-pair">
          <label className="field"><span>Name</span>
            <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder="e.g. Revenue dropped" aria-label="Alert name" />
          </label>
          <label className="field"><span>Saved trend</span>
            <select value={insightId} onChange={(e) => setInsightId(e.target.value ? Number(e.target.value) : '')} aria-label="Saved trend">
              <option value="">{trends.length ? 'Choose a saved trend…' : 'Save a trend insight first'}</option>
              {trends.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
            </select>
          </label>
        </div>
        <div className="row sentence alert-rule">
          <span>Notify when the</span>
          <select value={span} onChange={(e) => setSpan(e.target.value as AlertWindow)} aria-label="Window" className="op">
            {WINDOWS.map((w) => <option key={w.id} value={w.id}>{w.label}</option>)}
          </select>
          <select value={condition} onChange={(e) => setCondition(e.target.value as AlertCondition)} aria-label="Condition">
            {CONDITIONS.map((c) => <option key={c.id} value={c.id}>{c.label}</option>)}
          </select>
          <input type="number" step="any" className="narrow" value={threshold} onChange={(e) => setThreshold(e.target.value)} aria-label="Threshold" />
          {unit && <span>% compared with the window before</span>}
        </div>
        <label className="field"><span>Webhook URL</span>
          <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://hooks.example.com/reapdeep" aria-label="Webhook URL" />
        </label>
        <label className="switch">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
          <span className="switch-track" aria-hidden />
          Check automatically
        </label>
        <p className="footnote">The alert follows the trend's first series, or its formula when it has one; breakdowns are ignored. It is checked every 10 minutes for an hourly window, hourly for a day and every 6 hours for a week.</p>
        <FormMessage error={error} />
      </Card>
    </form>
  )
}
