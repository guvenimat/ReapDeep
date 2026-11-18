import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Annotation, Interval } from '../../api/types'
import { Icon } from '../../components/icons'
import { useQuery } from '../../lib/hooks'

export interface ChartAnnotation extends Annotation { index: number }

const dayOf = (bucket: string) => bucket.slice(0, 10)

function addDays(day: string, days: number) {
  const d = new Date(`${day}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() + days)
  return d.toISOString().slice(0, 10)
}

export function annotationIndex(buckets: string[], interval: Interval, day: string): number {
  if (interval === 'month') return buckets.findIndex((b) => b.slice(0, 7) === day.slice(0, 7))
  if (interval === 'week') {
    for (let i = buckets.length - 1; i >= 0; i--) {
      const start = dayOf(buckets[i])
      if (start <= day) return day < addDays(start, 7) ? i : -1
    }
    return -1
  }
  return buckets.findIndex((b) => dayOf(b) === day)
}

export function useAnnotations(buckets: string[], interval: Interval) {
  const [version, setVersion] = useState(0)
  const from = buckets.length ? dayOf(buckets[0]) : ''
  const to = buckets.length ? addDays(dayOf(buckets[buckets.length - 1]), interval === 'month' ? 31 : interval === 'week' ? 6 : 0) : ''
  const q = useQuery((signal) => (from ? api.annotations(from, to, signal) : Promise.resolve({ annotations: [] })), [from, to, version])
  const notes: ChartAnnotation[] = (q.data?.annotations ?? [])
    .map((a) => ({ ...a, index: annotationIndex(buckets, interval, a.day) }))
    .filter((a) => a.index >= 0)
  return { notes, from, to, reload: () => setVersion((v) => v + 1) }
}

export function AnnotationNotes({ notes, from, to, onChange }: { notes: ChartAnnotation[]; from: string; to: string; onChange: () => void }) {
  const [today] = useState(() => new Date().toISOString().slice(0, 10))
  const [day, setDay] = useState('')
  const [content, setContent] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const chosen = day || (to && to < today ? to : today)

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError(undefined)
    try {
      await fn()
      onChange()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  const add = (e: FormEvent) => {
    e.preventDefault()
    if (!content.trim()) return
    void run(async () => {
      await api.createAnnotation({ day: chosen, content: content.trim() })
      setContent('')
    })
  }

  return (
    <div className="annotations" data-annotations="">
      {notes.length > 0 && (
        <ul className="annotation-list">
          {notes.map((n) => (
            <li key={n.id} data-annotation-id={n.id}>
              <span className="annotation-day">{n.day}</span>
              <span className="annotation-text">{n.content}</span>
              {n.created_by && <span className="muted">{n.created_by}</span>}
              <button type="button" className="icon-button" aria-label={`Delete note of ${n.day}`} title="Delete note" disabled={busy}
                onClick={() => run(() => api.deleteAnnotation(n.id))}><Icon name="trash" /></button>
            </li>
          ))}
        </ul>
      )}
      <form className="annotation-form" onSubmit={add}>
        <input type="date" aria-label="Note date" value={chosen} min={from || undefined} onChange={(e) => setDay(e.target.value)} required />
        <input aria-label="Note" placeholder="Add a note to the chart, e.g. campaign launched" maxLength={300} value={content}
          onChange={(e) => setContent(e.target.value)} />
        <button type="submit" disabled={busy || !content.trim()}><Icon name="plus" />Add note</button>
      </form>
      {error && <p className="form-message error" role="alert">{error}</p>}
    </div>
  )
}
