import { useState } from 'react'
import { errorMessage } from '../../api/client'
import type { AiAnswer, AiVerdict } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card } from '../../components/ui'
import { dateTime } from '../../lib/format'

const VERDICTS: Record<AiVerdict['decision'], { label: string; tone: 'good' | 'muted' | 'accent' | 'danger' }> = {
  ship: { label: 'Ship', tone: 'good' },
  stop: { label: 'Stop', tone: 'danger' },
  keep_running: { label: 'Keep running', tone: 'accent' },
  no_difference: { label: 'No clear difference', tone: 'muted' },
  investigate: { label: 'Investigate first', tone: 'danger' },
}

function List({ title, items }: { title: string; items: string[] }) {
  if (items.length === 0) return null
  return (
    <div className="ai-section">
      <div className="eyebrow">{title}</div>
      <ul>{items.map((t, i) => <li key={i}>{t}</li>)}</ul>
    </div>
  )
}

export function AiAnswerView({ answer }: { answer: AiAnswer }) {
  const verdict = answer.verdict ? VERDICTS[answer.verdict.decision] : undefined
  return (
    <div className="ai-answer" data-ai-answer="">
      {answer.verdict && verdict && (
        <div className="ai-verdict">
          <Badge tone={verdict.tone}>{verdict.label}{answer.verdict.variant ? `: ${answer.verdict.variant}` : ''}</Badge>
          <span className="muted small">{answer.verdict.reason} This decision comes from fixed rules on the statistics, not from the model.</span>
        </div>
      )}
      <p className="ai-summary">{answer.summary}</p>
      <List title="Findings" items={answer.findings} />
      {answer.recommendation && (
        <div className="ai-section">
          <div className="eyebrow">Recommendation</div>
          <p>{answer.recommendation}</p>
        </div>
      )}
      <List title="Caveats" items={answer.caveats} />
      <List title="Next steps" items={answer.next_steps} />
      <p className="footnote">
        Written by {answer.model} from aggregated numbers only{answer.cached ? ` · cached answer from ${dateTime(answer.generated_at)}` : ''} · check the figures before acting on it.
      </p>
    </div>
  )
}

export function ExplainCard({ title = 'AI explanation', hint, load }: { title?: string; hint: string; load: () => Promise<AiAnswer> }) {
  const [answer, setAnswer] = useState<AiAnswer>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const run = async () => {
    setBusy(true)
    setError(undefined)
    try {
      setAnswer(await load())
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Card className="ai-card" title={<><Icon name="sparkles" />{title}</>}
      actions={<button type="button" onClick={run} disabled={busy}>{busy ? 'Thinking…' : answer ? 'Explain again' : 'Explain'}</button>}>
      {error && <p className="form-message error" role="alert">{error}</p>}
      {answer ? <AiAnswerView answer={answer} /> : !error && <p className="editor-hint">{hint}</p>}
    </Card>
  )
}
