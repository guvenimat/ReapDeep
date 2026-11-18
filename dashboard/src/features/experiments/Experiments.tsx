import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Experiment, ExperimentComparison, ExperimentInput, ExperimentMetric, ExperimentResults, ExperimentStatus, ExperimentVariantResult, MetricKind } from '../../api/types'
import { SuggestInput } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Badge, Card, FormMessage, IconButton, Segmented, StatTile, Status } from '../../components/ui'
import { downloadCsv, experimentCsv } from '../../lib/csv'
import { ago, dateTime, duration, num } from '../../lib/format'
import { useQuery } from '../../lib/hooks'
import { ExplainCard } from '../ai/AiAnswer'

const KINDS: { id: MetricKind; label: string }[] = [
  { id: 'conversion', label: 'Conversion' },
  { id: 'funnel', label: 'Funnel' },
  { id: 'revenue', label: 'Revenue per person' },
]

const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const signed = (n: number, digits = 1) => `${n > 0 ? '+' : n < 0 ? '−' : ''}${Math.abs(n).toLocaleString('en-US', { maximumFractionDigits: digits, minimumFractionDigits: digits })}`
const rate = (r: number) => `${(r * 100).toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`
const pValue = (p: number | null) => (p === null ? '—' : p < 0.0001 ? '< 0.0001' : p.toFixed(4))

export function describeMetric(m: ExperimentMetric) {
  const within = m.window_seconds ? ` within ${duration(m.window_seconds)}` : ''
  if (m.kind === 'funnel') return `Funnel ${(m.steps ?? []).join(' → ')}${within}`
  if (m.kind === 'revenue') return `Sum of ${m.property} on ${m.event} per person${within}`
  return `Did ${m.event}${within}`
}

function StatusBadge({ status }: { status: ExperimentStatus }) {
  if (status === 'running') return <Badge tone="good">Running</Badge>
  if (status === 'stopped') return <Badge tone="muted">Stopped</Badge>
  return <Badge tone="accent">Draft</Badge>
}

const day = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' }) : '—')

export function Experiments({ id, canManage, navigate }: { id?: string; canManage: boolean; navigate: (id?: string) => void }) {
  if (id === 'new' && canManage) return <ExperimentEditor onSaved={(x) => navigate(String(x.id))} onCancel={() => navigate(undefined)} />
  if (id && id !== 'new') return <ExperimentDetail key={id} id={Number(id)} canManage={canManage} navigate={navigate} />
  return <ExperimentList canManage={canManage} navigate={navigate} />
}

function ExperimentList({ canManage, navigate }: { canManage: boolean; navigate: (id?: string) => void }) {
  const [version, setVersion] = useState(0)
  const [message, setMessage] = useState<{ text: string; error?: boolean }>()
  const list = useQuery((signal) => api.experiments(signal), [version])
  const experiments = list.data?.experiments ?? []

  const remove = async (x: Experiment) => {
    if (!window.confirm(`Delete the experiment "${x.name}"? The flag and its events stay.`)) return
    try {
      await api.deleteExperiment(x.id)
      setMessage({ text: `Deleted "${x.name}"` })
      setVersion((v) => v + 1)
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    }
  }

  return (
    <Card testId="experiments-card" title="Experiments" meta="Compare the variants of a multivariate flag on a goal; exposures come from the SDK's $feature_flag_called events"
      actions={canManage ? <button type="button" className="primary" onClick={() => navigate('new')} data-testid="new-experiment"><Icon name="plus" />New experiment</button> : undefined}>
      <FormMessage error={message?.error ? message.text : undefined} success={message?.error ? undefined : message?.text} />
      <Status state={list} empty={experiments.length === 0} emptyTitle="No experiments yet"
        emptyText="Create a multivariate feature flag, then an experiment that measures which variant converts or earns more.">
        <div className="table-wrap">
          <table data-testid="experiments-table">
            <thead>
              <tr><th>Experiment</th><th>Flag</th><th>Goal</th><th>Status</th><th>Dates</th>{canManage && <th><span className="sr-only">Actions</span></th>}</tr>
            </thead>
            <tbody>
              {experiments.map((x) => (
                <tr key={x.id} className="experiment-row" data-experiment-id={x.id}>
                  <td>
                    <button type="button" className="link strong" onClick={() => navigate(String(x.id))}>{x.name}</button>
                    {x.description && <span className="cell-sub">{x.description}</span>}
                  </td>
                  <td>
                    <code className="chip">{x.flag_key}</code>
                    <span className="cell-sub">{x.variants.map((v) => `${v.key} ${v.percentage}%`).join(' · ')}</span>
                  </td>
                  <td>{describeMetric(x.metric)}</td>
                  <td><StatusBadge status={x.status} /></td>
                  <td className="nowrap muted">{day(x.start_date)} – {x.end_date ? day(x.end_date) : x.status === 'running' ? 'now' : '—'}</td>
                  {canManage && (
                    <td className="actions">
                      <IconButton icon="trash" label={`Delete ${x.name}`} tone="danger" disabled={x.status === 'running'} onClick={() => void remove(x)} />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
    </Card>
  )
}

function ExperimentDetail({ id, canManage, navigate }: { id: number; canManage: boolean; navigate: (id?: string) => void }) {
  const [version, setVersion] = useState(0)
  const [editing, setEditing] = useState(false)
  const [message, setMessage] = useState<{ text: string; error?: boolean }>()
  const detail = useQuery((signal) => api.experiment(id, signal), [id, version])
  const x = detail.data

  const act = async (fn: () => Promise<Experiment>, done: string) => {
    setMessage(undefined)
    try {
      await fn()
      setMessage({ text: done })
      setVersion((v) => v + 1)
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    }
  }

  if (x && editing)
    return <ExperimentEditor experiment={x} onCancel={() => setEditing(false)} onSaved={() => { setEditing(false); setVersion((v) => v + 1) }} />

  const actions = (
    <>
      <button type="button" onClick={() => navigate(undefined)}><Icon name="chevron-left" />All experiments</button>
      {x && canManage && x.status !== 'running' && <button type="button" onClick={() => setEditing(true)} data-testid="edit-experiment">Edit</button>}
      {x && canManage && x.status === 'draft' && (
        <button type="button" className="primary" data-testid="start-experiment"
          onClick={() => window.confirm(`Start "${x.name}"? The variants of ${x.flag_key} are locked until you stop it.`) && void act(() => api.startExperiment(x.id), 'Experiment started')}>Start</button>
      )}
      {x && canManage && x.status === 'running' && (
        <button type="button" data-testid="stop-experiment"
          onClick={() => window.confirm(`Stop "${x.name}"? Results keep the data up to now and the flag's variants unlock.`) && void act(() => api.stopExperiment(x.id), 'Experiment stopped')}>Stop</button>
      )}
    </>
  )

  return (
    <div className="stack">
      <Card testId="experiment-card" title={x ? <>{x.name} <StatusBadge status={x.status} /></> : 'Experiment'}
        meta={x && <>Flag <code className="chip">{x.flag_key}</code>{!x.flag_enabled && ' (disabled)'} · {describeMetric(x.metric)} · compared with <code className="chip">{x.control_variant}</code></>}
        actions={actions}>
        <FormMessage error={message?.error ? message.text : undefined} success={message?.error ? undefined : message?.text} />
        <Status state={detail}>
          {x && (
            <>
              {x.description && <p className="experiment-description">{x.description}</p>}
              <p className="footnote">
                {x.start_date ? <>From {dateTime(x.start_date)} to {x.end_date ? dateTime(x.end_date) : 'now'}. </> : 'Not started yet; results appear once it has a start date. '}
                {x.status === 'running' && <><Icon name="lock" size={13} /> Variants of {x.flag_key} are locked while it runs. </>}
                Updated {ago(x.updated_at)}{x.updated_by ? ` by ${x.updated_by}` : ''}.
              </p>
            </>
          )}
        </Status>
      </Card>
      {x?.start_date && <ExperimentResultsCard experiment={x} version={version} />}
    </div>
  )
}

function ExperimentResultsCard({ experiment, version }: { experiment: Experiment; version: number }) {
  const results = useQuery((signal) => api.experimentResults(experiment.id, signal), [experiment.id, version])
  const r = results.data
  const revenue = experiment.metric.kind === 'revenue'
  const exposed = r?.variants.reduce((s, v) => s + v.persons, 0) ?? 0

  return (
    <Card testId="experiment-results" title="Results"
      meta={r && `${r.test === 'welch_t' ? "Welch's t-test" : 'Two-proportion z-test'} against ${r.control_variant} · 95% confidence intervals · ${dateTime(r.date_from)} – ${dateTime(r.date_to)}`}
      actions={r && (
        <button type="button" className="icon-button" onClick={() => downloadCsv(experimentCsv(r))}
          aria-label="Download CSV" title="Download CSV" data-testid="experiment-export-csv"><Icon name="download" /></button>
      )}>
      <Status state={results} empty={exposed === 0} emptyTitle="No exposures yet"
        emptyText={`Persons are counted from their first $feature_flag_called event for ${experiment.flag_key} in the experiment's dates.`}>
        {r && (
          <div className="stack">
            {r.srm?.warning && (
              <div className="notice warn" role="alert" data-testid="srm-warning">
                <Icon name="bell" />
                <span><strong>Sample ratio mismatch.</strong> Exposed persons do not match the flag's split ({r.variants.map((v) => `${v.key} ${v.percentage}%`).join(', ')}):
                  chi-square {r.srm.chi_square.toFixed(2)}, p = {pValue(r.srm.p_value)} &lt; {r.srm.threshold}. Check how the flag is read before trusting these results.</span>
              </div>
            )}
            <div className="stats">
              <StatTile label="Exposed persons" value={num(exposed)} hint="First exposure inside the dates" testId="experiment-exposed" />
              <StatTile label="Excluded" value={num(r.excluded_persons)} hint="Saw more than one variant" />
              <StatTile label="Sample ratio" value={r.srm ? (r.srm.warning ? 'Mismatch' : 'OK') : '—'} hint={r.srm ? `p = ${pValue(r.srm.p_value)}` : 'Needs exposures'} />
              <StatTile label="Goal" value={revenue ? 'Revenue' : experiment.metric.kind === 'funnel' ? 'Funnel' : 'Conversion'} hint={describeMetric(experiment.metric)} />
            </div>
            <ResultsTable results={r} revenue={revenue} />
            <ExplainCard title="AI interpretation" feature="experiment_explain" load={() => api.explainExperiment(experiment.id)}
              hint="A plain-language reading of these results with a rule-based decision (ship, keep running, stop) and follow-up ideas." />
          </div>
        )}
      </Status>
    </Card>
  )
}

function ResultsTable({ results, revenue }: { results: ExperimentResults; revenue: boolean }) {
  const values = results.variants.flatMap((v) => (v.comparison ? [v.comparison.relative_low, v.comparison.relative_high, v.comparison.relative_lift] : []))
    .filter((n): n is number => n !== null)
  const extent = Math.max(0.05, ...values.map(Math.abs))
  const value = (v: ExperimentVariantResult) => (revenue ? money.format(v.mean) : rate(v.mean))
  const diff = (n: number) => (revenue ? signed(n, 2) : `${signed(n * 100)} pp`)

  return (
    <div className="table-wrap">
      <table className="experiment-table">
        <thead>
          <tr>
            <th>Variant</th><th className="num">Persons</th><th className="num">{revenue ? 'Buyers' : 'Converted'}</th>
            <th className="num">{revenue ? 'Mean per person' : 'Conversion rate'}</th><th className="num">Difference (95% CI)</th>
            <th>Relative lift (95% CI)</th><th className="num">p-value</th><th>Result</th>
          </tr>
        </thead>
        <tbody>
          {results.variants.map((v) => (
            <tr key={v.key} className="experiment-variant" data-variant={v.key}>
              <td><code className="chip">{v.key}</code>{v.is_control && <span className="cell-sub">control · {v.percentage}%</span>}{!v.is_control && <span className="cell-sub">{v.percentage}%</span>}</td>
              <td className="num">{num(v.persons)}</td>
              <td className="num">{num(v.conversions)}</td>
              <td className="num strong">{value(v)}{revenue && v.standard_deviation !== null && <span className="cell-sub">sd {money.format(v.standard_deviation)}</span>}</td>
              <td className="num">{v.comparison ? <>{diff(v.comparison.difference)}<CiText c={v.comparison} format={diff} /></> : <span className="muted">—</span>}</td>
              <td>{v.comparison ? <LiftBar c={v.comparison} extent={extent} /> : <span className="muted">{v.is_control ? 'baseline' : 'not enough data'}</span>}</td>
              <td className="num">{v.comparison ? pValue(v.comparison.p_value) : '—'}</td>
              <td>{v.comparison ? <Verdict c={v.comparison} /> : null}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function CiText({ c, format }: { c: ExperimentComparison; format: (n: number) => string }) {
  if (c.difference_low === null || c.difference_high === null) return null
  return <span className="cell-sub">{format(c.difference_low)} to {format(c.difference_high)}</span>
}

function LiftBar({ c, extent }: { c: ExperimentComparison; extent: number }) {
  if (c.relative_lift === null) return <span className="muted">—</span>
  const pos = (n: number) => `${Math.min(100, Math.max(0, 50 + (n / extent) * 50))}%`
  const low = c.relative_low ?? c.relative_lift
  const high = c.relative_high ?? c.relative_lift
  const tone = c.significant ? (c.relative_lift > 0 ? 'good' : 'bad') : 'neutral'
  return (
    <div className="lift">
      <span className="lift-value">{signed(c.relative_lift * 100)}%</span>
      <span className="lift-track" aria-hidden>
        <span className="lift-zero" />
        <span className={`lift-range ${tone}`} style={{ left: pos(low), width: `calc(${pos(high)} - ${pos(low)})` }} />
        <span className={`lift-point ${tone}`} style={{ left: pos(c.relative_lift) }} />
      </span>
      {c.relative_low !== null && c.relative_high !== null && <span className="cell-sub">{signed(c.relative_low * 100)}% to {signed(c.relative_high * 100)}%</span>}
    </div>
  )
}

function Verdict({ c }: { c: ExperimentComparison }) {
  if (c.p_value === null) return <Badge tone="muted">No test</Badge>
  if (!c.significant) return <Badge tone="muted">Not significant</Badge>
  return c.difference > 0 ? <Badge tone="good">Significant win</Badge> : <Badge tone="danger">Significant loss</Badge>
}

const hours = (seconds?: number | null) => (seconds ? String(Math.round((seconds / 3600) * 100) / 100) : '')
const toDay = (iso: string | null) => (iso ? iso.slice(0, 10) : '')

function ExperimentEditor({ experiment, onSaved, onCancel }: { experiment?: Experiment; onSaved: (x: Experiment) => void; onCancel: () => void }) {
  const flags = useQuery((signal) => api.flags(signal), [])
  const multivariate = (flags.data?.flags ?? []).filter((f) => f.variants.length >= 2)
  const [name, setName] = useState(experiment?.name ?? '')
  const [description, setDescription] = useState(experiment?.description ?? '')
  const [flagId, setFlagId] = useState<number | undefined>(experiment?.flag_id)
  const [control, setControl] = useState(experiment?.control_variant ?? '')
  const [kind, setKind] = useState<MetricKind>(experiment?.metric.kind ?? 'conversion')
  const [event, setEvent] = useState(experiment?.metric.event ?? '')
  const [steps, setSteps] = useState<string[]>(experiment?.metric.steps ?? ['', ''])
  const [property, setProperty] = useState(experiment?.metric.property ?? '')
  const [windowHours, setWindowHours] = useState(hours(experiment?.metric.window_seconds))
  const [startDate, setStartDate] = useState(toDay(experiment?.start_date ?? null))
  const [endDate, setEndDate] = useState(toDay(experiment?.end_date ?? null))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const flag = multivariate.find((f) => f.id === flagId) ?? multivariate[0]
  const selectedFlag = flagId ?? flag?.id
  const variants = flag?.variants ?? experiment?.variants ?? []
  const valid = name.trim() !== '' && selectedFlag !== undefined &&
    (kind === 'funnel' ? steps.filter((s) => s.trim()).length >= 2 : event.trim() !== '' && (kind !== 'revenue' || property.trim() !== ''))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      const windowSeconds = windowHours.trim() === '' ? null : Math.round(Number(windowHours) * 3600)
      const metric: ExperimentMetric = kind === 'funnel'
        ? { kind, steps: steps.map((s) => s.trim()).filter(Boolean), window_seconds: windowSeconds }
        : { kind, event: event.trim(), window_seconds: windowSeconds, property: kind === 'revenue' ? property.trim() : null }
      const body: ExperimentInput = {
        name: name.trim(), description: description.trim(), flag_id: selectedFlag!, control_variant: variants.some((v) => v.key === control) ? control : '',
        metric,
        start_date: startDate === toDay(experiment?.start_date ?? null) && experiment ? undefined : startDate,
        end_date: endDate === toDay(experiment?.end_date ?? null) && experiment ? undefined : endDate,
      }
      onSaved(experiment ? await api.updateExperiment(experiment.id, body) : await api.createExperiment(body))
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} data-testid="experiment-editor">
      <Card title={experiment ? `Edit ${experiment.name}` : 'New experiment'} actions={<>
        <button type="button" onClick={onCancel}>Cancel</button>
        <button type="submit" className="primary" disabled={busy || !valid}>{busy ? 'Saving…' : experiment ? 'Save changes' : 'Create experiment'}</button>
      </>}>
        <div className="flag-editor">
          <div className="field-pair">
            <label className="field" htmlFor="experiment-name"><span>Name</span>
              <input id="experiment-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder="New checkout flow" />
            </label>
            <label className="field" htmlFor="experiment-description"><span>Hypothesis</span>
              <input id="experiment-description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={1000} placeholder="The one-page checkout sells more" />
            </label>
          </div>

          <div className="field-pair">
            <label className="field" htmlFor="experiment-flag"><span>Feature flag</span>
              <select id="experiment-flag" value={selectedFlag ?? ''} disabled={experiment !== undefined && experiment.status !== 'draft'}
                onChange={(e) => { setFlagId(Number(e.target.value)); setControl('') }}>
                {multivariate.length === 0 && <option value="">No multivariate flags yet</option>}
                {multivariate.map((f) => <option key={f.id} value={f.id}>{f.key} ({f.variants.map((v) => `${v.key} ${v.percentage}%`).join(', ')})</option>)}
              </select>
            </label>
            <label className="field" htmlFor="experiment-control"><span>Control variant</span>
              <select id="experiment-control" value={variants.some((v) => v.key === control) ? control : variants[0]?.key ?? ''} onChange={(e) => setControl(e.target.value)}>
                {variants.map((v) => <option key={v.key} value={v.key}>{v.key}</option>)}
              </select>
            </label>
          </div>

          <div className="editor-block">
            <span className="editor-title">Goal</span>
            <span className="editor-hint">Counted per person, only after their first exposure to the flag. Persons who saw more than one variant are left out.</span>
            <Segmented label="Goal metric" value={kind} onChange={setKind} options={KINDS} />
            {kind === 'funnel' ? (
              <>
                {steps.map((s, i) => (
                  <div className="row" key={i}>
                    <span className="muted small">{i + 1}.</span>
                    <SuggestInput list="events" placeholder="event" value={s} onChange={(v) => setSteps(steps.map((x, j) => (j === i ? v : x)))} aria-label={`Step ${i + 1} event`} />
                    {steps.length > 2 && <IconButton icon="close" label="Remove step" onClick={() => setSteps(steps.filter((_, j) => j !== i))} />}
                  </div>
                ))}
                {steps.length < 8 && <button type="button" className="add-button" onClick={() => setSteps([...steps, ''])}>+ Add step</button>}
              </>
            ) : (
              <div className="row">
                <SuggestInput list="events" placeholder="event, e.g. purchase" value={event} onChange={setEvent} aria-label="Goal event" />
                {kind === 'revenue' && <SuggestInput list="event-props" placeholder="amount property" value={property} onChange={setProperty} aria-label="Amount property" />}
              </div>
            )}
            <div className="row sentence">
              <span>{kind === 'funnel' ? 'Complete the steps within' : 'Count it within'}</span>
              <input type="number" min={0} step="any" className="narrow" value={windowHours} onChange={(e) => setWindowHours(e.target.value)} aria-label="Window in hours"
                placeholder={kind === 'funnel' ? '168' : 'any'} />
              <span>hours {kind === 'funnel' ? '(default 7 days)' : 'of the first exposure (empty: until the end)'}</span>
            </div>
          </div>

          <div className="field-pair">
            <label className="field" htmlFor="experiment-start"><span>Start date</span>
              <input id="experiment-start" type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} />
            </label>
            <label className="field" htmlFor="experiment-end"><span>End date</span>
              <input id="experiment-end" type="date" value={endDate} onChange={(e) => setEndDate(e.target.value)} />
            </label>
          </div>
          <p className="footnote">Leave the start date empty to start counting when you press Start. Dates are in the project's time zone. While the experiment runs the flag's variants and percentages cannot change; rollout, targeting and on/off still can.</p>
          <FormMessage error={error} />
        </div>
      </Card>
    </form>
  )
}
