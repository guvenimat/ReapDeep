import { createContext, useContext, type ReactNode } from 'react'
import type { Breakdown, Filter, Metadata, Op, Scope } from '../api/types'
import { Icon } from './icons'
import { IconButton } from './ui'

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

export function EditorSection({ title, hint, children }: { title: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <div className="editor-block">
      <div className="editor-title">{title}</div>
      {children}
      {hint && <p className="editor-hint">{hint}</p>}
    </div>
  )
}

export function AddButton({ children, onClick }: { children: ReactNode; onClick: () => void }) {
  return (
    <button type="button" className="add-button" onClick={onClick}>
      <Icon name="plus" size={14} />{children}
    </button>
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
    <EditorSection title="Filters">
      {filters.map((f, i) => (
        <div className="editor-item" key={i}>
          <div className="row">
            <ScopeSelect value={f.scope} onChange={(scope) => update(i, { scope })} />
            <input list={propList(f.scope)} placeholder="property" value={f.property} onChange={(e) => update(i, { property: e.target.value })} aria-label="Filter property" />
            <IconButton icon="close" label="Remove filter" onClick={() => onChange(filters.filter((_, k) => k !== i))} />
          </div>
          <div className="row">
            <select value={f.op} onChange={(e) => update(i, { op: e.target.value as Op })} aria-label="Operator" className="op">
              {OPS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
            </select>
            {OPS.find((o) => o.id === f.op)?.needsValue !== false && (
              <input placeholder="value" value={f.value ?? ''} onChange={(e) => update(i, { value: e.target.value })} aria-label="Filter value" />
            )}
          </div>
        </div>
      ))}
      <AddButton onClick={() => onChange([...filters, { scope: 'event', property: '', op: 'eq', value: '' }])}>Add filter</AddButton>
    </EditorSection>
  )
}

export function BreakdownEditor({ value, onChange, maxLimit = 8 }: { value?: Breakdown; onChange: (b?: Breakdown) => void; maxLimit?: number }) {
  return (
    <EditorSection title="Breakdown">
      {value ? (
        <div className="editor-item">
          <div className="row">
            <ScopeSelect value={value.scope} onChange={(scope) => onChange({ ...value, scope })} />
            <input list={propList(value.scope)} placeholder="property" value={value.property} onChange={(e) => onChange({ ...value, property: e.target.value })} aria-label="Breakdown property" />
            <IconButton icon="close" label="Remove breakdown" onClick={() => onChange(undefined)} />
          </div>
          <label className="inline">Top
            <input type="number" min={1} max={maxLimit} value={value.limit ?? 5} className="narrow"
              onChange={(e) => onChange({ ...value, limit: Math.min(maxLimit, Math.max(1, Number(e.target.value) || 1)) })} />
            values
          </label>
        </div>
      ) : (
        <AddButton onClick={() => onChange({ scope: 'event', property: '$browser', limit: 5 })}>Add breakdown</AddButton>
      )}
    </EditorSection>
  )
}

function ScopeSelect({ value, onChange }: { value: Scope; onChange: (s: Scope) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as Scope)} aria-label="Scope" className="scope">
      <option value="event">Event</option>
      <option value="person">Person</option>
    </select>
  )
}

export const completeFilters = (filters: Filter[]) =>
  filters.filter((f) => f.property.trim() && (f.op === 'is_set' || (f.value ?? '').trim() !== ''))
export const completeBreakdown = (b?: Breakdown) => (b && b.property.trim() ? b : undefined)
