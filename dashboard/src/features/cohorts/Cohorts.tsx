import { useEffect, useState } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Cohort, CohortPerson } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card, FormMessage, IconButton, StatTile, Status } from '../../components/ui'
import { ago, initials, num } from '../../lib/format'
import { useQuery } from '../../lib/hooks'
import { CohortEditor } from './CohortEditor'
import { describeCondition } from './cohortRules'

const POLL_MS = 3000

export function Cohorts({ id, navigate, openPerson, onChanged }: {
  id?: string; navigate: (id?: string) => void; openPerson: (id?: string) => void; onChanged: () => void
}) {
  const saved = (c: Cohort) => {
    onChanged()
    navigate(String(c.id))
  }
  if (id === 'new') return <CohortEditor onSaved={saved} onCancel={() => navigate(undefined)} />
  if (id) return <CohortDetail key={id} id={Number(id)} navigate={navigate} openPerson={openPerson} onChanged={onChanged} onSaved={saved} />
  return <CohortList navigate={navigate} />
}

const isSettled = (c: Cohort) => c.status === 'error' || (c.status === 'ready' && c.calculated_at !== null)

function usePolling(active: boolean) {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    if (!active) return
    const t = setTimeout(() => setTick((n) => n + 1), POLL_MS)
    return () => clearTimeout(t)
  }, [active, tick])
  return tick
}

function StatusBadge({ cohort }: { cohort: Cohort }) {
  if (cohort.status === 'error') return <Badge tone="danger">Failed</Badge>
  if (cohort.member_count === null || cohort.status !== 'ready') return <Badge tone="accent">Calculating</Badge>
  if (cohort.calculated_at === null) return <Badge tone="accent">Refreshing</Badge>
  return <Badge tone="good">Ready</Badge>
}

function CohortList({ navigate }: { navigate: (id?: string) => void }) {
  const [tick, setTick] = useState(0)
  const list = useQuery((signal) => api.cohorts(signal), [tick])
  const pending = list.data?.cohorts.some((c) => !isSettled(c)) ?? false
  const poll = usePolling(pending)
  useEffect(() => { if (poll) setTick((t) => t + 1) }, [poll])

  const create = (
    <button type="button" className="primary" onClick={() => navigate('new')} data-testid="new-cohort"><Icon name="plus" />New cohort</button>
  )
  return (
    <Card title="Cohorts" meta={list.data && `${num(list.data.cohorts.length)} total · use them as filters and breakdowns in every analysis`} actions={create}>
      <Status state={list} empty={list.data?.cohorts.length === 0} emptyTitle="No cohorts yet"
        emptyText="Group people by what they did (for example, viewed 3+ products but did not buy) or by their properties.">
        <div className="table-wrap">
          <table data-testid="cohorts-table">
            <thead><tr><th>Name</th><th>Type</th><th className="num">Persons</th><th>Status</th><th>Calculated</th><th>Created by</th></tr></thead>
            <tbody>
              {list.data?.cohorts.map((c) => (
                <tr key={c.id} className="clickable" onClick={() => navigate(String(c.id))}>
                  <td>
                    <a className="insight-link" href={`#/cohorts/${c.id}`} onClick={(e) => { e.preventDefault(); navigate(String(c.id)) }}>
                      <Icon name="users" />{c.name}
                    </a>
                    {c.description && <div className="muted small cohort-description">{c.description}</div>}
                  </td>
                  <td><Badge>{c.kind === 'dynamic' ? 'Dynamic' : 'Static'}</Badge></td>
                  <td className="num strong">{c.member_count === null ? '—' : num(c.member_count)}</td>
                  <td><StatusBadge cohort={c} /></td>
                  <td className="nowrap muted">{ago(c.calculated_at)}</td>
                  <td className="muted">{c.created_by ?? '-'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
    </Card>
  )
}

function CohortDetail({ id, navigate, openPerson, onChanged, onSaved }: {
  id: number; navigate: (id?: string) => void; openPerson: (id?: string) => void; onChanged: () => void; onSaved: (c: Cohort) => void
}) {
  const [tick, setTick] = useState(0)
  const [editing, setEditing] = useState(false)
  const [error, setError] = useState<string>()
  const cohort = useQuery((signal) => api.cohort(id, signal), [id, tick])
  const c = cohort.data
  const poll = usePolling(!!c && !isSettled(c))
  useEffect(() => { if (poll) setTick((t) => t + 1) }, [poll])

  if (editing && c) return <CohortEditor cohort={c} onSaved={(s) => { setEditing(false); setTick((t) => t + 1); onSaved(s) }} onCancel={() => setEditing(false)} />

  const recalculate = async () => {
    setError(undefined)
    try {
      await api.recalculateCohort(id)
      setTick((t) => t + 1)
    } catch (err) {
      setError(errorMessage(err))
    }
  }
  const remove = async () => {
    if (!c) return
    setError(undefined)
    try {
      const usage = await api.cohortUsage(id)
      const used = usage.insights.length
        ? `\n\nThese saved insights use it and will stop working:\n${usage.insights.map((i) => `• ${i.name}`).join('\n')}`
        : ''
      if (!window.confirm(`Delete cohort "${c.name}"?${used}`)) return
      await api.deleteCohort(id)
      onChanged()
      navigate(undefined)
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  const trendHref = `#/trends?${new URLSearchParams({ range: '30d', q: JSON.stringify({ series: [{ event: '$pageview', metric: 'unique_users' }], filters: [{ scope: 'cohort', property: String(id), op: 'in' }], deduplicate: false, chart: 'line' }) })}`

  return (
    <div className="stack" data-testid="cohort-detail">
      <div className="dashboard-bar">
        <button type="button" className="back-link" onClick={() => navigate(undefined)}><Icon name="chevron-left" />All cohorts</button>
        <div className="dashboard-heading">
          <h2 data-testid="cohort-name">{c?.name ?? 'Cohort'}</h2>
          {c && <StatusBadge cohort={c} />}
        </div>
        <span className="spacer" />
        <button type="button" onClick={recalculate} disabled={!c}><Icon name="refresh" />Recalculate</button>
        <button type="button" onClick={() => setEditing(true)} disabled={!c}><Icon name="edit" />Edit</button>
        <IconButton icon="trash" tone="danger" label="Delete cohort" onClick={remove} disabled={!c} />
      </div>
      <FormMessage error={error} />
      <Status state={cohort}>
        {c && (
          <>
            {c.description && <p className="muted">{c.description}</p>}
            <div className="stats">
              <StatTile label="Persons" value={c.member_count === null ? '—' : num(c.member_count)} testId="cohort-members"
                hint={c.kind === 'static' ? `${num(c.static_members ?? 0)} ids in the list` : 'Current members'} />
              <StatTile label="Type" value={c.kind === 'dynamic' ? 'Dynamic' : 'Static'} hint={c.kind === 'dynamic' ? 'Recalculated automatically' : 'Fixed list of ids'} />
              <StatTile label="Last calculated" value={ago(c.calculated_at)} hint={c.calculation_ms === null ? 'Not calculated yet' : `took ${num(c.calculation_ms)} ms`} />
            </div>
            {c.last_error && <div className="notice error" role="alert">Last calculation failed: {c.last_error}</div>}
            {c.kind === 'dynamic' && c.definition && (
              <Card title="Rules" actions={<a className="insight-link" href={trendHref}><Icon name="trends" />Analyze in Trends</a>}>
                <div className="rule-summary">
                  {c.definition.groups.map((g, gi) => (
                    <div key={gi} className="rule-summary-group">
                      {gi > 0 && <span className="or-pill">or</span>}
                      <ul>{g.conditions.map((cond, ci) => <li key={ci}><span className="muted">{ci === 0 ? 'Persons who ' : 'and '}</span>{describeCondition(cond)}</li>)}</ul>
                    </div>
                  ))}
                </div>
              </Card>
            )}
            {c.member_count !== null && <CohortPersons id={id} version={c.calculated_at} openPerson={openPerson} />}
          </>
        )}
      </Status>
    </div>
  )
}

function CohortPersons({ id, version, openPerson }: { id: number; version: string | null; openPerson: (id?: string) => void }) {
  const [pages, setPages] = useState<CohortPerson[][]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const first = useQuery((signal) => api.cohortPersons(id, undefined, signal), [id, version])
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    setPages([])
    setCursor(first.data?.next_cursor ?? null)
  }, [first.data])

  const more = async () => {
    if (!cursor) return
    setLoading(true)
    try {
      const page = await api.cohortPersons(id, cursor)
      setPages((p) => [...p, page.persons])
      setCursor(page.next_cursor)
    } finally {
      setLoading(false)
    }
  }

  const persons = [...(first.data?.persons ?? []), ...pages.flat()]
  return (
    <Card title="Persons in this cohort" className="flush-table">
      <Status state={first} empty={first.data?.persons.length === 0} emptyTitle="Nobody matches yet">
        <div className="table-wrap">
          <table data-testid="cohort-persons">
            <thead><tr><th>Person</th><th>distinct_ids</th><th>Properties</th></tr></thead>
            <tbody>
              {persons.map((p) => (
                <tr key={p.id} className={p.is_identified ? 'clickable' : undefined} onClick={p.is_identified ? () => openPerson(p.id) : undefined}>
                  <td>
                    <div className="person-cell">
                      <span className="avatar sm" aria-hidden>{initials(label(p))}</span>
                      <div>
                        <div className="strong">{label(p)}</div>
                        <div className="mono muted small">{p.is_identified ? p.id : 'anonymous'}</div>
                      </div>
                    </div>
                  </td>
                  <td className="num muted">{num(p.distinct_ids.length)}</td>
                  <td className="truncate muted">{Object.entries(p.properties).slice(0, 3).map(([k, v]) => `${k}: ${String(v)}`).join(' · ')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {cursor && <div className="load-more"><button type="button" onClick={more} disabled={loading}>{loading ? 'Loading…' : 'Load more'}</button></div>}
      </Status>
    </Card>
  )
}

const label = (p: CohortPerson) => String(p.properties.email ?? p.properties.name ?? p.distinct_ids.find((d) => !d.startsWith('anon')) ?? p.id)
