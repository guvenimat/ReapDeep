import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { AiAnswer, AiExperimentIdea, AiQueryAnswer } from '../../api/types'
import { ACTION_PREFIX } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Card } from '../../components/ui'
import { AiAnswerView } from './AiAnswer'
import { useAiFeature } from './AiAccess'

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

function Digest({ canManage }: { canManage: boolean }) {
  const [answer, setAnswer] = useState<AiAnswer>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const run = async () => {
    setBusy(true)
    setError(undefined)
    try {
      setAnswer(await api.aiDigest())
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Card className="ai-card" title={<><Icon name="sparkles" />Weekly strategy note</>}
      meta="Traffic, customer segments, recommendations and experiments of the last 7 days, with experiment ideas"
      actions={<button type="button" className="primary" onClick={run} disabled={busy}>{busy ? 'Writing…' : answer ? 'Write again' : 'Write the note'}</button>}>
      {error && <p className="form-message error" role="alert">{error}</p>}
      {answer ? (
        <>
          <AiAnswerView answer={answer} />
          {(answer.experiments?.length ?? 0) > 0 && (
            <div className="ai-section">
              <div className="eyebrow">Experiment ideas</div>
              <ul className="ai-ideas">{answer.experiments!.map((i) => <Idea key={i.flag_key + i.name} idea={i} canManage={canManage} />)}</ul>
              <p className="footnote">A draft creates a disabled 50/50 flag and an experiment on the goal event; nothing changes on the site until you enable the flag and start the experiment.</p>
            </div>
          )}
        </>
      ) : !error && <p className="editor-hint">Gathers this week's numbers and asks the model for a short note. Only aggregates leave ReapDeep.</p>}
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
