import { Fragment, useState } from 'react'
import { api, type EventRow } from './api'
import { dateTime, ms, num } from './format'
import { readState, toDateRange, type rangeFromParams } from './route'
import { JsonView, Status, useDebounced, useQuery } from './ui'

interface EventsState { event: string; distinct_id: string; person_id: string; limit: number }
const DEFAULT: EventsState = { event: '', distinct_id: '', person_id: '', limit: 50 }

export function Events({ params, range, onChange, openPerson }: {
  params: URLSearchParams
  range: ReturnType<typeof rangeFromParams>
  onChange: (q: string) => void
  openPerson: (id: string) => void
}) {
  const state = readState(params, DEFAULT)
  const set = (patch: Partial<EventsState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const [nonce, setNonce] = useState(0)

  const request = useDebounced(JSON.stringify({ ...state, ...toDateRange(range) }))
  const q = useQuery((signal) => {
    const p = JSON.parse(request)
    return api.events({ ...p, event: p.event.trim(), distinct_id: p.distinct_id.trim(), person_id: p.person_id.trim() }, signal)
  }, [request, nonce])

  return (
    <div className="stack">
      <div className="card toolbar">
        <input list="dl-events" placeholder="event" value={state.event} onChange={(e) => set({ event: e.target.value })} aria-label="Event" />
        <input placeholder="distinct_id" value={state.distinct_id} onChange={(e) => set({ distinct_id: e.target.value })} aria-label="distinct_id" />
        <input placeholder="person_id" value={state.person_id} onChange={(e) => set({ person_id: e.target.value })} aria-label="person_id" />
        <select value={state.limit} onChange={(e) => set({ limit: Number(e.target.value) })} aria-label="Limit">
          {[50, 100, 200].map((n) => <option key={n} value={n}>{n} rows</option>)}
        </select>
        <button onClick={() => setNonce((n) => n + 1)}>Refresh</button>
        <span className="muted">{q.data && `${num(q.data.events.length)} event · ${ms(q.data.took_ms)}`}</span>
      </div>
      <Status state={q} empty={q.data?.events.length === 0}>
        <div className="card">
          <EventsTable events={q.data?.events ?? []} openPerson={openPerson} />
        </div>
      </Status>
    </div>
  )
}

const page = (e: EventRow) => e.columns.pathname ?? e.columns.current_url ?? ''
const client = (e: EventRow) => [e.columns.browser, e.columns.os].filter(Boolean).join(' / ')

export function EventsTable({ events, openPerson }: { events: EventRow[]; openPerson?: (id: string) => void }) {
  const [open, setOpen] = useState<string | null>(null)
  return (
    <div className="table-wrap">
      <table className="events" data-testid="events-table">
        <thead>
          <tr><th>Time</th><th>Event</th><th>distinct_id</th><th>Person</th><th>Page</th><th>Browser / OS</th></tr>
        </thead>
        <tbody>
          {events.map((e) => {
            const key = e.event_id + e.timestamp
            const isOpen = open === key
            return (
              <Fragment key={key}>
                <tr className={`clickable${isOpen ? ' open' : ''}`} onClick={() => setOpen(isOpen ? null : key)} aria-expanded={isOpen}>
                  <td className="nowrap tabular">{dateTime(e.timestamp)}</td>
                  <td className="strong">{e.event}</td>
                  <td className="mono">{e.distinct_id}</td>
                  <td className="mono">
                    {openPerson ? (
                      <a href={`#/persons/${encodeURIComponent(e.resolved_person_id)}`}
                        onClick={(ev) => { ev.preventDefault(); ev.stopPropagation(); openPerson(e.resolved_person_id) }}>
                        {short(e.resolved_person_id)}
                      </a>
                    ) : short(e.resolved_person_id)}
                  </td>
                  <td className="truncate" title={e.columns.current_url}>{page(e)}</td>
                  <td className="nowrap">{client(e)}</td>
                </tr>
                {isOpen && (
                  <tr className="detail">
                    <td colSpan={6}>
                      <div className="detail-grid">
                        <dl>
                          <dt>event_id</dt><dd className="mono">{e.event_id}</dd>
                          <dt>timestamp (UTC)</dt><dd className="mono">{e.timestamp}</dd>
                          <dt>resolved_person_id</dt><dd className="mono">{e.resolved_person_id}</dd>
                          {Object.entries(e.columns).map(([k, v]) => (
                            <Fragment key={k}><dt>{k}</dt><dd className="mono">{v}</dd></Fragment>
                          ))}
                        </dl>
                        <div>
                          <div className="editor-title">properties</div>
                          <JsonView value={e.properties ?? {}} />
                        </div>
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

const short = (id: string) => (id.length > 18 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id)
