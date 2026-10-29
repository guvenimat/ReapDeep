import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import type { Breakdown, Filter, Metadata, Op, Scope } from './api'

export interface QueryState<T> { data?: T; error?: string; loading: boolean }

export function useQuery<T>(fn: (signal: AbortSignal) => Promise<T>, deps: unknown[]): QueryState<T> {
  const [state, setState] = useState<QueryState<T>>({ loading: true })
  useEffect(() => {
    const ctl = new AbortController()
    setState((s) => ({ data: s.data, loading: true }))
    fn(ctl.signal)
      .then((data) => !ctl.signal.aborted && setState({ data, loading: false }))
      .catch((e: Error) => !ctl.signal.aborted && setState({ error: e.message, loading: false }))
    return () => ctl.abort()
  }, deps)
  return state
}

export function useDebounced<T>(value: T, ms = 400): T {
  const [v, setV] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return v
}

export const MetaContext = createContext<Metadata | undefined>(undefined)

export function MetaDatalists() {
  const meta = useContext(MetaContext)
  return (
    <>
      <datalist id="dl-events">{meta?.events.map((e) => <option key={e.name} value={e.name} />)}</datalist>
      <datalist id="dl-event-props">
        {meta?.event_columns.map((c) => <option key={c} value={c} />)}
        {meta?.event_properties.filter((p) => !meta.event_columns.includes(p.name)).map((p) => <option key={p.name} value={p.name} label={p.type ?? undefined} />)}
      </datalist>
      <datalist id="dl-person-props">{meta?.person_properties.map((p) => <option key={p.name} value={p.name} label={p.type ?? undefined} />)}</datalist>
    </>
  )
}

const propList = (scope: Scope) => (scope === 'person' ? 'dl-person-props' : 'dl-event-props')

const OPS: { id: Op; label: string; needsValue: boolean }[] = [
  { id: 'eq', label: '=', needsValue: true },
  { id: 'neq', label: '≠', needsValue: true },
  { id: 'contains', label: 'contains', needsValue: true },
  { id: 'is_set', label: 'is set', needsValue: false },
]

export function FilterEditor({ filters, onChange }: { filters: Filter[]; onChange: (f: Filter[]) => void }) {
  const update = (i: number, patch: Partial<Filter>) => onChange(filters.map((f, k) => (k === i ? { ...f, ...patch } : f)))
  return (
    <div className="editor-block">
      <div className="editor-title">Filters</div>
      {filters.map((f, i) => (
        <div className="row" key={i}>
          <ScopeSelect value={f.scope} onChange={(scope) => update(i, { scope })} />
          <input list={propList(f.scope)} placeholder="property" value={f.property} onChange={(e) => update(i, { property: e.target.value })} />
          <select value={f.op} onChange={(e) => update(i, { op: e.target.value as Op })} aria-label="Operator">
            {OPS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
          </select>
          {OPS.find((o) => o.id === f.op)?.needsValue !== false && (
            <input placeholder="value" value={f.value ?? ''} onChange={(e) => update(i, { value: e.target.value })} />
          )}
          <button className="icon" onClick={() => onChange(filters.filter((_, k) => k !== i))} aria-label="Remove filter">×</button>
        </div>
      ))}
      <button className="link" onClick={() => onChange([...filters, { scope: 'event', property: '', op: 'eq', value: '' }])}>+ Add filter</button>
    </div>
  )
}

export function BreakdownEditor({ value, onChange, maxLimit = 8 }: { value?: Breakdown; onChange: (b?: Breakdown) => void; maxLimit?: number }) {
  return (
    <div className="editor-block">
      <div className="editor-title">Breakdown</div>
      {value ? (
        <div className="row">
          <ScopeSelect value={value.scope} onChange={(scope) => onChange({ ...value, scope })} />
          <input list={propList(value.scope)} placeholder="property" value={value.property} onChange={(e) => onChange({ ...value, property: e.target.value })} />
          <label className="inline">top
            <input type="number" min={1} max={maxLimit} value={value.limit ?? 5} className="narrow"
              onChange={(e) => onChange({ ...value, limit: Math.min(maxLimit, Math.max(1, Number(e.target.value) || 1)) })} />
          </label>
          <button className="icon" onClick={() => onChange(undefined)} aria-label="Remove breakdown">×</button>
        </div>
      ) : (
        <button className="link" onClick={() => onChange({ scope: 'event', property: '$browser', limit: 5 })}>+ Add breakdown</button>
      )}
    </div>
  )
}

function ScopeSelect({ value, onChange }: { value: Scope; onChange: (s: Scope) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as Scope)} aria-label="Scope">
      <option value="event">event</option>
      <option value="person">person</option>
    </select>
  )
}

export const completeFilters = (filters: Filter[]) =>
  filters.filter((f) => f.property.trim() && (f.op === 'is_set' || (f.value ?? '').trim() !== ''))
export const completeBreakdown = (b?: Breakdown) => (b && b.property.trim() ? b : undefined)

export function Status({ state, empty, children }: { state: QueryState<unknown>; empty?: boolean; children: ReactNode }) {
  if (state.error) return <div className="notice error" role="alert">⚠ {state.error}</div>
  if (!state.data) return <div className="notice">Loading…</div>
  return (
    <div className={state.loading ? 'stale' : undefined}>
      {empty ? <div className="notice">No data in this range.</div> : children}
    </div>
  )
}

export function JsonView({ value }: { value: unknown }) {
  return <pre className="json">{JSON.stringify(value, null, 2)}</pre>
}
