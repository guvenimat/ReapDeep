import { createContext, useContext, type ReactNode } from 'react'
import type { Breakdown, Cohort, Filter, Metadata, Op, Scope } from '../api/types'
import { Icon } from './icons'
import { IconButton, Segmented } from './ui'

export const MetaContext = createContext<Metadata | undefined>(undefined)
export const CohortsContext = createContext<Cohort[]>([])

export const MAX_BREAKDOWN_COHORTS = 5

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

export const propList = (scope: Scope) => (scope === 'person' ? 'dl-person-props' : 'dl-event-props')

export const OPS: { id: Op; label: string; needsValue: boolean }[] = [
  { id: 'eq', label: '=', needsValue: true },
  { id: 'neq', label: '≠', needsValue: true },
  { id: 'contains', label: 'contains', needsValue: true },
  { id: 'is_set', label: 'is set', needsValue: false },
]

export function OpSelect({ value, onChange }: { value: Op; onChange: (op: Op) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as Op)} aria-label="Operator" className="op">
      {OPS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
    </select>
  )
}

export const needsValue = (op: Op) => OPS.find((o) => o.id === op)?.needsValue !== false

function CohortSelect({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const cohorts = useContext(CohortsContext)
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} aria-label="Cohort" className="grow-select">
      <option value="">Choose a cohort…</option>
      {cohorts.map((c) => (
        <option key={c.id} value={String(c.id)} disabled={c.member_count === null}>
          {c.name}{c.member_count === null ? ' (calculating)' : ''}
        </option>
      ))}
    </select>
  )
}

export function FilterEditor({ filters, onChange }: { filters: Filter[]; onChange: (f: Filter[]) => void }) {
  const update = (i: number, patch: Partial<Filter>) => onChange(filters.map((f, k) => (k === i ? { ...f, ...patch } : f)))
  const changeScope = (i: number, scope: Scope) =>
    update(i, scope === 'cohort' ? { scope, property: '', op: 'in', value: undefined } : { scope, op: filters[i].scope === 'cohort' ? 'eq' : filters[i].op })
  return (
    <EditorSection title="Filters">
      {filters.map((f, i) => (
        <div className="editor-item" key={i}>
          <div className="row">
            <ScopeSelect value={f.scope} onChange={(scope) => changeScope(i, scope)} allowCohort />
            {f.scope === 'cohort'
              ? <CohortSelect value={f.property} onChange={(property) => update(i, { property })} />
              : <input list={propList(f.scope)} placeholder="property" value={f.property} onChange={(e) => update(i, { property: e.target.value })} aria-label="Filter property" />}
            <IconButton icon="close" label="Remove filter" onClick={() => onChange(filters.filter((_, k) => k !== i))} />
          </div>
          {f.scope === 'cohort' ? (
            <Segmented<Op> size="sm" label="Cohort membership" value={f.op} onChange={(op) => update(i, { op })}
              options={[{ id: 'in', label: 'In cohort' }, { id: 'not_in', label: 'Not in cohort' }]} />
          ) : (
            <div className="row">
              <OpSelect value={f.op} onChange={(op) => update(i, { op })} />
              {needsValue(f.op) && <input placeholder="value" value={f.value ?? ''} onChange={(e) => update(i, { value: e.target.value })} aria-label="Filter value" />}
            </div>
          )}
        </div>
      ))}
      <AddButton onClick={() => onChange([...filters, { scope: 'event', property: '', op: 'eq', value: '' }])}>Add filter</AddButton>
    </EditorSection>
  )
}

export function BreakdownEditor({ value, onChange, maxLimit = 8, allowCohort }: {
  value?: Breakdown; onChange: (b?: Breakdown) => void; maxLimit?: number; allowCohort?: boolean
}) {
  const cohorts = useContext(CohortsContext)
  const changeScope = (scope: Scope) =>
    onChange(scope === 'cohort' ? { scope, property: '', cohort_ids: [] } : { scope, property: value?.scope === 'cohort' ? '$browser' : value?.property ?? '', limit: 5 })
  const toggleCohort = (id: number) => {
    const ids = value?.cohort_ids ?? []
    onChange({ ...value!, cohort_ids: ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id] })
  }
  return (
    <EditorSection title="Breakdown">
      {value ? (
        <div className="editor-item">
          <div className="row">
            <ScopeSelect value={value.scope} onChange={changeScope} allowCohort={allowCohort} />
            {value.scope !== 'cohort' && (
              <input list={propList(value.scope)} placeholder="property" value={value.property} onChange={(e) => onChange({ ...value, property: e.target.value })} aria-label="Breakdown property" />
            )}
            <span className="spacer" />
            <IconButton icon="close" label="Remove breakdown" onClick={() => onChange(undefined)} />
          </div>
          {value.scope === 'cohort' ? (
            <div className="check-list" role="group" aria-label="Cohorts to compare">
              {cohorts.length === 0 && <span className="editor-hint">No cohorts yet. Create one in the Cohorts tab.</span>}
              {cohorts.map((c) => {
                const checked = value.cohort_ids?.includes(c.id) ?? false
                const full = (value.cohort_ids?.length ?? 0) >= MAX_BREAKDOWN_COHORTS
                return (
                  <label key={c.id} className="check">
                    <input type="checkbox" checked={checked} disabled={c.member_count === null || (!checked && full)} onChange={() => toggleCohort(c.id)} />
                    <span>{c.name}</span>
                  </label>
                )
              })}
              <span className="editor-hint">Up to {MAX_BREAKDOWN_COHORTS} cohorts; a person can be in more than one.</span>
            </div>
          ) : (
            <label className="inline">Top
              <input type="number" min={1} max={maxLimit} value={value.limit ?? 5} className="narrow"
                onChange={(e) => onChange({ ...value, limit: Math.min(maxLimit, Math.max(1, Number(e.target.value) || 1)) })} />
              values
            </label>
          )}
        </div>
      ) : (
        <AddButton onClick={() => onChange({ scope: 'event', property: '$browser', limit: 5 })}>Add breakdown</AddButton>
      )}
    </EditorSection>
  )
}

function ScopeSelect({ value, onChange, allowCohort }: { value: Scope; onChange: (s: Scope) => void; allowCohort?: boolean }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as Scope)} aria-label="Scope" className="scope">
      <option value="event">Event</option>
      <option value="person">Person</option>
      {allowCohort && <option value="cohort">Cohort</option>}
    </select>
  )
}

export const completeFilters = (filters: Filter[]) =>
  filters.filter((f) => f.scope === 'cohort'
    ? f.property !== '' && (f.op === 'in' || f.op === 'not_in')
    : f.property.trim() && (f.op === 'is_set' || (f.value ?? '').trim() !== ''))

export const completeBreakdown = (b?: Breakdown) => {
  if (!b) return undefined
  if (b.scope === 'cohort') return b.cohort_ids?.length ? { scope: b.scope, property: '', cohort_ids: b.cohort_ids } : undefined
  return b.property.trim() ? b : undefined
}
