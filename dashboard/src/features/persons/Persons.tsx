import { useState } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Person } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card, IconButton, JsonView, Status } from '../../components/ui'
import { dateTime, initials, num } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { EventsTable } from '../events/Events'

export function Persons({ id, openPerson, canManage }: { id?: string; openPerson: (id?: string) => void; canManage: boolean }) {
  const [deletionsVersion, setDeletionsVersion] = useState(0)
  const [search, setSearch] = useState('')
  const [onlyIdentified, setOnlyIdentified] = useState(true)
  const debounced = useDebounced(search)
  const list = useQuery((signal) => api.persons({ search: debounced.trim(), limit: 100, identified: onlyIdentified || undefined }, signal), [debounced, onlyIdentified])

  return (
    <div className={id ? 'split' : 'stack'}>
      <div className="stack">
        <div className="toolbar">
          <div className="search-input grow">
            <Icon name="search" />
            <input placeholder="Search by email, property or distinct_id" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search persons" />
          </div>
          <label className="switch">
            <input type="checkbox" checked={onlyIdentified} onChange={(e) => setOnlyIdentified(e.target.checked)} />
            <span className="switch-track" aria-hidden />
            Identified only
          </label>
        </div>
        <Status state={list} empty={list.data?.persons.length === 0} emptyText="No persons match this search.">
          <Card className="flush">
            <div className="table-wrap">
              <table data-testid="persons-table" className={id ? 'compact' : undefined}>
                <thead><tr><th>Person</th><th>Created</th><th>Properties</th></tr></thead>
                <tbody>
                  {list.data?.persons.map((p) => (
                    <tr key={p.id} className={`clickable${p.id === id ? ' open' : ''}`} onClick={() => openPerson(p.id)}>
                      <td>
                        <div className="person-cell">
                          <span className="avatar sm" aria-hidden>{initials(displayName(p))}</span>
                          <div>
                            <div className="strong">{displayName(p)}</div>
                            <div className="mono muted small">{p.id}</div>
                          </div>
                        </div>
                      </td>
                      <td className="nowrap tabular muted">{dateTime(p.created_at)}</td>
                      <td className="truncate muted">{summary(p)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
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
    <Card title="Deletion requests" meta="Processed by a periodic batch job">
      <Status state={list}>
        <div className="table-wrap">
          <table data-testid="deletion-requests">
            <thead><tr><th>Target</th><th>Requested</th><th>By</th><th>Status</th></tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td><span className="muted small">{r.target_type}</span> <span className="mono">{r.target_id}</span></td>
                  <td className="nowrap tabular">{dateTime(r.requested_at)}</td>
                  <td className="muted">{r.requested_by ?? '-'}</td>
                  <td className="nowrap">{r.processed_at ? <Badge tone="good">Deleted {dateTime(r.processed_at)}</Badge> : <Badge>Pending</Badge>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
    </Card>
  )
}

function PersonDetail({ id, onClose, canManage, onDeletionRequested }: {
  id: string; onClose: () => void; canManage: boolean; onDeletionRequested: () => void
}) {
  const person = useQuery((signal) => api.person(id, signal), [id])
  const [deletion, setDeletion] = useState<{ id: string; state: 'sending' | 'done' | string }>()
  const deletionState = deletion?.id === id ? deletion.state : undefined
  const from = new Date(Date.now() - 365 * 86400_000).toISOString()
  const events = useQuery((signal) => api.events({ person_id: id, limit: 200, date_from: from }, signal), [id])

  const requestDeletion = async () => {
    if (!window.confirm('Delete this person, all of their distinct_ids and all of their events? This cannot be undone.')) return
    setDeletion({ id, state: 'sending' })
    try {
      await api.requestDeletion('person', id)
      setDeletion({ id, state: 'done' })
      onDeletionRequested()
    } catch (err) {
      setDeletion({ id, state: errorMessage(err) })
    }
  }

  const p = person.data
  return (
    <aside className="card detail-panel" data-testid="person-detail">
      <div className="person-hero">
        <span className="avatar lg" aria-hidden>{p ? initials(displayName(p)) : ''}</span>
        <div className="person-hero-text">
          <h2>{p ? displayName(p) : 'Person'}</h2>
          {p && <div className="row">{p.is_identified ? <Badge tone="accent">Identified</Badge> : <Badge>Anonymous</Badge>}<span className="muted small">since {dateTime(p.created_at)}</span></div>}
        </div>
        <IconButton icon="close" label="Close" onClick={onClose} />
      </div>
      <Status state={person}>
        {p && (
          <>
            <dl className="kv">
              <dt>person_id</dt><dd className="mono">{p.id}</dd>
              <dt>distinct_ids</dt>
              <dd><ul className="ids" data-testid="person-distinct-ids">{p.distinct_ids.map((d) => <li key={d} className="mono">{d}</li>)}</ul></dd>
            </dl>
            <div className="editor-title">Properties</div>
            <JsonView value={p.properties} />
            {canManage && (
              <div className="danger-zone" data-testid="person-deletion">
                {deletionState === 'done' ? (
                  <span className="small">Deletion requested. The person and their events are removed in the next batch run.</span>
                ) : (
                  <>
                    <div className="danger-text">
                      <span className="strong">Delete person data</span>
                      <span className="muted small">Removes the person, their distinct_ids and all of their events.</span>
                    </div>
                    <button className="danger" onClick={requestDeletion} disabled={deletionState === 'sending'} data-testid="request-deletion">
                      <Icon name="trash" />{deletionState === 'sending' ? 'Requesting…' : 'Delete person data'}
                    </button>
                    {deletionState && deletionState !== 'sending' && <span className="form-message error">{deletionState}</span>}
                  </>
                )}
              </div>
            )}
          </>
        )}
      </Status>
      <div className="editor-title">Event history {events.data && <span className="muted">· {num(events.data.events.length)}</span>}</div>
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
