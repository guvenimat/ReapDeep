import { useState } from 'react'
import { api, type DeletionRequest, type Person } from './api'
import { EventsTable } from './Events'
import { dateTime, num } from './format'
import { JsonView, Status, useDebounced, useQuery } from './ui'

export function Persons({ id, openPerson, canManage }: { id?: string; openPerson: (id?: string) => void; canManage: boolean }) {
  const [deletionsVersion, setDeletionsVersion] = useState(0)
  const [search, setSearch] = useState('')
  const [onlyIdentified, setOnlyIdentified] = useState(true)
  const debounced = useDebounced(search)
  const list = useQuery((signal) => api.persons({ search: debounced.trim(), limit: 100, identified: onlyIdentified || undefined }, signal), [debounced, onlyIdentified])

  return (
    <div className={id ? 'split' : 'stack'}>
      <div className="stack">
        <div className="card toolbar">
          <input placeholder="Search: email, property or distinct_id" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search persons" className="grow" />
          <label className="inline check">
            <input type="checkbox" checked={onlyIdentified} onChange={(e) => setOnlyIdentified(e.target.checked)} />
            Identified only
          </label>
        </div>
        <Status state={list} empty={list.data?.persons.length === 0}>
          <div className="card">
            <div className="table-wrap">
              <table data-testid="persons-table">
                <thead><tr><th>Person</th><th>Created</th><th>Properties</th></tr></thead>
                <tbody>
                  {list.data?.persons.map((p) => (
                    <tr key={p.id} className={`clickable${p.id === id ? ' open' : ''}`} onClick={() => openPerson(p.id)}>
                      <td>
                        <div className="strong">{displayName(p)}</div>
                        <div className="mono muted small">{p.id}</div>
                      </td>
                      <td className="nowrap tabular">{dateTime(p.created_at)}</td>
                      <td className="truncate muted">{summary(p)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </Status>
        <DeletionRequests version={deletionsVersion} />
      </div>
      {id && <PersonDetail id={id} onClose={() => openPerson(undefined)} canManage={canManage} onDeletionRequested={() => setDeletionsVersion((v) => v + 1)} />}
    </div>
  )
}

function DeletionRequests({ version }: { version: number }) {
  const list = useQuery((signal) => api.deletionRequests(signal), [version])
  const rows = list.data?.deletion_requests ?? []
  if (!list.loading && !list.error && rows.length === 0) return null
  return (
    <div className="card">
      <div className="card-head"><h2>Deletion requests</h2><span className="muted small">Processed by a periodic batch job</span></div>
      <Status state={list}>
        <div className="table-wrap">
          <table data-testid="deletion-requests">
            <thead><tr><th>Target</th><th>Requested</th><th>By</th><th>Status</th></tr></thead>
            <tbody>
              {rows.map((r: DeletionRequest) => (
                <tr key={r.id}>
                  <td><span className="muted small">{r.target_type}</span> <span className="mono">{r.target_id}</span></td>
                  <td className="nowrap tabular">{dateTime(r.requested_at)}</td>
                  <td className="muted">{r.requested_by ?? '-'}</td>
                  <td className="nowrap">{r.processed_at ? `Deleted ${dateTime(r.processed_at)}` : 'Pending'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
    </div>
  )
}

function PersonDetail({ id, onClose, canManage, onDeletionRequested }: {
  id: string; onClose: () => void; canManage: boolean; onDeletionRequested: () => void
}) {
  const person = useQuery((signal) => api.person(id, signal), [id])
  const [deletion, setDeletion] = useState<{ id: string; state: 'sending' | 'done' | string }>()
  const deletionState = deletion?.id === id ? deletion.state : undefined

  const requestDeletion = async () => {
    if (!window.confirm('Delete this person, all of their distinct_ids and all of their events? This cannot be undone.')) return
    setDeletion({ id, state: 'sending' })
    try {
      await api.requestDeletion('person', id)
      setDeletion({ id, state: 'done' })
      onDeletionRequested()
    } catch (err) {
      setDeletion({ id, state: err instanceof Error ? err.message : String(err) })
    }
  }
  const from = new Date(Date.now() - 365 * 86400_000).toISOString()
  const events = useQuery((signal) => api.events({ person_id: id, limit: 200, date_from: from }, signal), [id])

  return (
    <aside className="card detail-panel" data-testid="person-detail">
      <div className="card-head">
        <h2>{person.data ? displayName(person.data) : 'Person'}</h2>
        <button className="icon" onClick={onClose} aria-label="Close">×</button>
      </div>
      <Status state={person}>
        {person.data && (
          <>
            <dl className="kv">
              <dt>person_id</dt><dd className="mono">{person.data.id}</dd>
              <dt>Status</dt><dd>{person.data.is_identified ? 'Identified' : 'Anonymous'}</dd>
              <dt>Created</dt><dd>{dateTime(person.data.created_at)}</dd>
              <dt>distinct_ids</dt>
              <dd><ul className="ids" data-testid="person-distinct-ids">{person.data.distinct_ids.map((d) => <li key={d} className="mono">{d}</li>)}</ul></dd>
            </dl>
            <div className="editor-title">properties</div>
            <JsonView value={person.data.properties} />
            {canManage && (
              <div className="danger-zone" data-testid="person-deletion">
                {deletionState === 'done' ? (
                  <span className="small">Deletion requested. The person and their events are removed in the next batch run.</span>
                ) : (
                  <>
                    <button className="danger" onClick={requestDeletion} disabled={deletionState === 'sending'} data-testid="request-deletion">
                      {deletionState === 'sending' ? 'Requesting...' : 'Delete person data'}
                    </button>
                    {deletionState && deletionState !== 'sending' && <span className="login-error">{deletionState}</span>}
                  </>
                )}
              </div>
            )}
          </>
        )}
      </Status>
      <div className="editor-title">Event history {events.data && `(${num(events.data.events.length)})`}</div>
      <Status state={events} empty={events.data?.events.length === 0}>
        <EventsTable events={events.data?.events ?? []} />
      </Status>
    </aside>
  )
}

function displayName(p: Person) {
  const props = p.properties as Record<string, unknown>
  return String(props.email ?? props.name ?? p.distinct_ids.find((d) => !/^[0-9a-f-]{36}$/.test(d)) ?? p.distinct_ids[0] ?? p.id)
}

const summary = (p: Person) =>
  Object.entries(p.properties).slice(0, 4).map(([k, v]) => `${k}: ${typeof v === 'object' ? JSON.stringify(v) : String(v)}`).join(' · ')
