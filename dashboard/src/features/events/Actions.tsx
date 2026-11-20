import { useContext, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { ActionDef } from '../../api/types'
import { ActionsContext, SuggestInput } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Card, FormMessage } from '../../components/ui'

const MAX_EVENTS = 20

export function Actions() {
  const { actions, reload } = useContext(ActionsContext)
  const [editing, setEditing] = useState<ActionDef | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [events, setEvents] = useState<string[]>([''])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const reset = () => {
    setEditing(null)
    setName('')
    setDescription('')
    setEvents([''])
  }
  const edit = (a: ActionDef) => {
    setEditing(a)
    setName(a.name)
    setDescription(a.description)
    setEvents([...a.events, ''])
    setError(undefined)
  }
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError(undefined)
    try {
      await fn()
      reload()
      reset()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  const save = (e: FormEvent) => {
    e.preventDefault()
    const body = { name: name.trim(), description: description.trim(), events: [...new Set(events.map((x) => x.trim()).filter(Boolean))] }
    void run(() => (editing ? api.updateAction(editing.id, body) : api.createAction(body)))
  }
  const setEvent = (i: number, v: string) => {
    const next = events.map((x, k) => (k === i ? v : x))
    setEvents(next[next.length - 1].trim() && next.length < MAX_EVENTS ? [...next, ''] : next)
  }

  return (
    <Card className="actions-card" title={<><Icon name="events" />Actions</>}
      meta="Name a group of events, then use it as action:Name in Trends and Funnels; changing an action updates every insight that uses it">
      {actions.length > 0 && (
        <ul className="action-list">
          {actions.map((a) => (
            <li key={a.id} data-action={a.name}>
              <span className="strong">{a.name}</span>
              <span className="action-events">{a.events.map((e) => <span key={e} className="chip">{e}</span>)}</span>
              {a.description && <span className="muted">{a.description}</span>}
              <span className="spacer" />
              <button type="button" className="icon-button" aria-label={`Edit ${a.name}`} title="Edit" onClick={() => edit(a)}><Icon name="edit" /></button>
              <button type="button" className="icon-button" aria-label={`Delete ${a.name}`} title="Delete" disabled={busy}
                onClick={() => window.confirm(`Delete the action "${a.name}"? Saved insights that use it will stop working; cohorts that use it block the delete.`) && run(() => api.deleteAction(a.id))}>
                <Icon name="trash" />
              </button>
            </li>
          ))}
        </ul>
      )}
      <form className="action-form" onSubmit={save}>
        <div className="row">
          <input required maxLength={100} placeholder="Action name, e.g. Checkout" value={name} onChange={(e) => setName(e.target.value)} aria-label="Action name" />
          <input maxLength={500} placeholder="Description (optional)" value={description} onChange={(e) => setDescription(e.target.value)} aria-label="Action description" />
        </div>
        <div className="row action-event-inputs">
          {events.map((ev, i) => (
            <SuggestInput key={i} list="events" placeholder={i === 0 ? 'first event' : 'or another event'} value={ev}
              aria-label={`Action event ${i + 1}`} onChange={(v) => setEvent(i, v)} />
          ))}
        </div>
        <div className="row">
          <button type="submit" className="primary" disabled={busy || !name.trim() || !events.some((x) => x.trim())}>
            {editing ? 'Save action' : <><Icon name="plus" />Create action</>}
          </button>
          {editing && <button type="button" onClick={reset}>Cancel</button>}
        </div>
      </form>
      <FormMessage error={error} />
    </Card>
  )
}
