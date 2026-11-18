import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { AiExperimentIdea, AiNote, AiQueryAnswer, DigestFrequency, DigestSchedule } from '../../api/types'
import { ACTION_PREFIX } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Card } from '../../components/ui'
import { AiAnswerView } from './AiAnswer'
import { useAiFeature } from './AiAccess'
import { useQuery } from '../../lib/hooks'
import { dateTime } from '../../lib/format'

type Item = { event?: string; action?: string; metric?: string; property?: string }
const ref = (i: Item) => (i.action ? ACTION_PREFIX + i.action : i.event ?? '')

export function queryHref(a: AiQueryAnswer): string {
  const q = a.query as Record<string, unknown> & { date_from?: string; date_to?: string }
  const range = q.date_from ? `range=custom&from=${encodeURIComponent(q.date_from.slice(0, 10))}${q.date_to ? `&to=${encodeURIComponent(q.date_to.slice(0, 10))}` : ''}` : 'range=30d'
  const state = a.kind === 'trends'
    ? {
        series: ((q.series as Item[]) ?? []).map((s) => ({ event: ref(s), metric: s.metric ?? 'total', ...(s.property ? { property: s.property } : {}) })),
        ...(q.interval ? { interval: q.interval } : {}), filters: q.filters ?? [], ...(q.breakdown ? { breakdown: q.breakdown } : {}),
        deduplicate: false, chart: 'line', compare: false,
      }
    : {
        steps: ((q.steps as Item[]) ?? []).map(ref), window_seconds: q.window_seconds ?? 7 * 86400, filters: q.filters ?? [],
        ...(q.breakdown ? { breakdown: q.breakdown } : {}), sampling: 1,
      }
  return `#/${a.kind}?${range}&q=${encodeURIComponent(JSON.stringify(state))}`
}

function Ask() {
  const [question, setQuestion] = useState('')
  const [answer, setAnswer] = useState<AiQueryAnswer>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const ask = async (e: FormEvent) => {
    e.preventDefault()
    if (!question.trim()) return
    setBusy(true)
    setError(undefined)
    setAnswer(undefined)
    try {
      setAnswer(await api.aiQuery(question.trim()))
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Card className="ai-card" title={<><Icon name="search" />Ask a question</>}
      meta="Turns a question into a trend or funnel you can open and adjust; the model sees event and property names, never people">
      <form className="ai-ask" onSubmit={ask}>
        <textarea rows={2} maxLength={500} value={question} onChange={(e) => setQuestion(e.target.value)} aria-label="Question"
          placeholder="e.g. How many people bought on mobile each day last month? / Funnel from product_viewed to purchase this week" />
        <button type="submit" className="primary" disabled={busy || !question.trim()}>{busy ? 'Thinking…' : 'Ask'}</button>
      </form>
      {error && <p className="form-message error" role="alert">{error}</p>}
      {answer && (
        <div className="ai-answer" data-ai-query={answer.kind}>
          <p className="ai-summary">{answer.explanation || `A ${answer.kind === 'trends' ? 'trend' : 'funnel'} for your question.`}</p>
          <a className="insight-link" href={queryHref(answer)} data-ai-open=""><Icon name={answer.kind} />Open in {answer.kind === 'trends' ? 'Trends' : 'Funnels'}</a>
          <details>
            <summary className="muted small">Request</summary>
            <pre className="json">{JSON.stringify(answer.query, null, 2)}</pre>
          </details>
          <p className="footnote">Written by {answer.model}{answer.cached ? ' · cached' : ''}; the analysis itself runs in ReapDeep.</p>
        </div>
      )}
    </Card>
  )
}

function Idea({ idea, canManage }: { idea: AiExperimentIdea; canManage: boolean }) {
  const [state, setState] = useState<{ busy?: boolean; error?: string; experimentId?: number }>({})
  const create = async () => {
    setState({ busy: true })
    try {
      const flag = await api.createFlag({
        key: idea.flag_key, name: idea.name, description: idea.hypothesis, enabled: false, rollout_percentage: 100,
        variants: [{ key: 'control', percentage: 50 }, { key: 'test', percentage: 50 }], conditions: [],
      })
      const x = await api.createExperiment({
        name: idea.name, description: idea.hypothesis, flag_id: flag.id, control_variant: 'control', metric: { kind: 'conversion', event: idea.goal_event },
      })
      setState({ experimentId: x.id })
    } catch (err) {
      setState({ error: errorMessage(err) })
    }
  }
  return (
    <li className="ai-idea" data-ai-idea={idea.flag_key}>
      <div>
        <span className="strong">{idea.name}</span>
        <span className="muted small">{idea.hypothesis} · goal: <span className="chip">{idea.goal_event}</span></span>
      </div>
      {state.experimentId
        ? <a href={`#/experiments/${state.experimentId}`}>Open draft</a>
        : canManage && <button type="button" onClick={create} disabled={state.busy}>Create draft experiment</button>}
      {state.error && <span className="form-message error">{state.error}</span>}
    </li>
  )
}

const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
const FREQUENCIES: { id: DigestFrequency; label: string }[] = [
  { id: 'off', label: 'Off' }, { id: 'daily', label: 'Every day' }, { id: 'weekly', label: 'Once a week' }, { id: 'monthly', label: 'Once a month' },
]

function describeSchedule(s: DigestSchedule) {
  const at = `${String(s.hour).padStart(2, '0')}:00 (${s.timezone})`
  if (s.frequency === 'daily') return `every day at ${at}, about the day before`
  if (s.frequency === 'weekly') return `every ${WEEKDAYS[s.weekday - 1]} at ${at}, about the last 7 days`
  if (s.frequency === 'monthly') return `on day ${s.month_day} of each month at ${at}, about the previous month`
  return 'not scheduled'
}

function ScheduleEditor({ canManage }: { canManage: boolean }) {
  const [version, setVersion] = useState(0)
  const schedule = useQuery((signal) => api.aiSchedule(signal), [version])
  const [draft, setDraft] = useState<{ frequency: DigestFrequency; weekday: number; month_day: number; hour: number; email: string }>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const s = schedule.data
  const d = draft ?? (s && { frequency: s.frequency, weekday: s.weekday, month_day: s.month_day, hour: s.hour, email: s.email_to.join(', ') })
  if (!s || !d) return null
  const set = (patch: Partial<NonNullable<typeof draft>>) => setDraft({ ...d, ...patch })
  const save = async () => {
    setBusy(true)
    setError(undefined)
    try {
      const email_to = d.email.split(/[,;\s]+/).map((x) => x.trim()).filter(Boolean)
      await api.setAiSchedule({ frequency: d.frequency, weekday: d.weekday, month_day: d.month_day, hour: d.hour, email_to })
      setDraft(undefined)
      setVersion((v) => v + 1)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="ai-section ai-schedule" data-ai-schedule={s.frequency}>
      <div className="eyebrow">Schedule</div>
      <p className="muted small">
        {s.frequency === 'off' ? 'Notes are written only when someone asks.' : `A note is written ${describeSchedule(s)}`}
        {s.next_run_at && ` · next: ${dateTime(s.next_run_at)}`}
        {s.email_to.length > 0 && ` · e-mailed to ${s.email_to.join(', ')}`}
      </p>
      {s.last_error && <p className="form-message error">Last run: {s.last_error}</p>}
      {canManage && (
        <div className="row ai-schedule-form">
          <select aria-label="Frequency" value={d.frequency} onChange={(e) => set({ frequency: e.target.value as DigestFrequency })}>
            {FREQUENCIES.map((f) => <option key={f.id} value={f.id}>{f.label}</option>)}
          </select>
          {d.frequency === 'weekly' && (
            <select aria-label="Day of the week" value={d.weekday} onChange={(e) => set({ weekday: Number(e.target.value) })}>
              {WEEKDAYS.map((w, k) => <option key={w} value={k + 1}>{w}</option>)}
            </select>
          )}
          {d.frequency === 'monthly' && (
            <select aria-label="Day of the month" value={d.month_day} onChange={(e) => set({ month_day: Number(e.target.value) })}>
              {Array.from({ length: 28 }, (_, k) => <option key={k} value={k + 1}>Day {k + 1}</option>)}
            </select>
          )}
          {d.frequency !== 'off' && (
            <>
              <select aria-label="Hour" value={d.hour} onChange={(e) => set({ hour: Number(e.target.value) })}>
                {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
              </select>
              {s.email_available
                ? <input aria-label="E-mail recipients" placeholder="E-mail to (optional), comma separated" value={d.email} onChange={(e) => set({ email: e.target.value })} />
                : <span className="muted small">No SMTP server: notes stay in the app.</span>}
            </>
          )}
          <button type="button" className="primary" onClick={save} disabled={busy}>{busy ? 'Saving…' : 'Save schedule'}</button>
        </div>
      )}
      {error && <p className="form-message error" role="alert">{error}</p>}
    </div>
  )
}

function Digest({ canManage }: { canManage: boolean }) {
  const [version, setVersion] = useState(0)
  const notes = useQuery((signal) => api.aiNotes(signal), [version])
  const [selected, setSelected] = useState<number>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const list = notes.data?.notes ?? []
  const note = list.find((n) => n.id === selected) ?? list[0]
  const run = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await api.aiDigest()
      setSelected(undefined)
      setVersion((v) => v + 1)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  const label = (n: AiNote) => `${dateTime(n.created_at)} · ${n.period_from} to ${n.period_to} · ${n.trigger === 'schedule' ? 'scheduled' : 'on request'}${n.emailed_to.length ? ' · e-mailed' : ''}`
  return (
    <Card className="ai-card" title={<><Icon name="sparkles" />Strategy note</>}
      meta="Traffic, customer segments, recommendations and experiments, with experiment ideas"
      actions={<button type="button" className="primary" onClick={run} disabled={busy}>{busy ? 'Writing…' : 'Write a note now'}</button>}>
      <ScheduleEditor canManage={canManage} />
      {error && <p className="form-message error" role="alert">{error}</p>}
      {list.length > 1 && (
        <select aria-label="Earlier notes" value={note?.id} onChange={(e) => setSelected(Number(e.target.value))} data-ai-notes={list.length}>
          {list.map((n) => <option key={n.id} value={n.id}>{label(n)}</option>)}
        </select>
      )}
      {note ? (
        <>
          {list.length === 1 && <p className="muted small">{label(note)}</p>}
          <AiAnswerView answer={note.answer} />
          {(note.answer.experiments?.length ?? 0) > 0 && (
            <div className="ai-section">
              <div className="eyebrow">Experiment ideas</div>
              <ul className="ai-ideas">{note.answer.experiments!.map((i) => <Idea key={`${note.id}-${i.flag_key}-${i.name}`} idea={i} canManage={canManage} />)}</ul>
              <p className="footnote">A draft creates a disabled 50/50 flag and an experiment on the goal event; nothing changes on the site until you enable the flag and start the experiment.</p>
            </div>
          )}
        </>
      ) : !error && <p className="editor-hint">No note yet. Write one now or set a schedule; every note is kept here (the last 50).</p>}
    </Card>
  )
}

export function AiPage({ canManage }: { canManage: boolean }) {
  const ask = useAiFeature('query')
  const digest = useAiFeature('digest')
  return (
    <div className="stack">
      {ask && <Ask />}
      {digest && <Digest canManage={canManage} />}
      {!ask && !digest && (
        <Card title={<><Icon name="sparkles" />AI insights</>}>
          <p className="editor-hint" data-ai-off="">Questions in plain language and the weekly strategy note are not turned on for this organization.
            Owners and admins manage AI features in Settings; the platform team decides which ones each organization can use.</p>
        </Card>
      )}
    </div>
  )
}
