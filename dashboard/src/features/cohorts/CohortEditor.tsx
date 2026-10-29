import { useState, type ChangeEvent, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Cohort, CohortCondition, CohortDefinition, CohortKind, CountOp, Filter } from '../../api/types'
import { AddButton, needsValue, OpSelect, propList } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Card, FormMessage, IconButton, Segmented } from '../../components/ui'
import { num } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import {
  cleanDefinition, COUNT_OPS, definitionProblem, emptyDefinition, MAX_CONDITIONS, MAX_GROUPS, MAX_WITHIN_DAYS, newCondition, parseIds,
} from './cohortRules'

const CONDITION_TYPES: { id: CohortCondition['type']; label: string }[] = [
  { id: 'performed_event', label: 'Did event' },
  { id: 'not_performed_event', label: 'Did not do event' },
  { id: 'person_property', label: 'Person property' },
]

export function CohortEditor({ cohort, onSaved, onCancel }: { cohort?: Cohort; onSaved: (c: Cohort) => void; onCancel: () => void }) {
  const [name, setName] = useState(cohort?.name ?? '')
  const [description, setDescription] = useState(cohort?.description ?? '')
  const [kind, setKind] = useState<CohortKind>(cohort?.kind ?? 'dynamic')
  const [definition, setDefinition] = useState<CohortDefinition>(cohort?.definition ?? emptyDefinition())
  const [membersText, setMembersText] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const ids = parseIds(membersText)
  const problem = kind === 'dynamic' ? definitionProblem(definition) : undefined
  const canSave = name.trim() !== '' && !busy && (kind === 'dynamic' ? !problem : cohort ? true : ids.length > 0)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!canSave) return
    setBusy(true)
    setError(undefined)
    try {
      const def = kind === 'dynamic' ? cleanDefinition(definition) : undefined
      let saved = cohort
        ? await api.updateCohort(cohort.id, { name: name.trim(), description: description.trim(), definition: def })
        : await api.createCohort({ name: name.trim(), description: description.trim(), kind, definition: def, members: kind === 'static' ? ids : undefined })
      if (cohort && kind === 'static' && ids.length > 0) saved = await api.replaceCohortMembers(cohort.id, ids)
      onSaved(saved)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  const readFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file) setMembersText(await file.text())
  }

  return (
    <form className="cohort-editor" onSubmit={submit} data-testid="cohort-editor">
      <Card title={cohort ? `Edit ${cohort.name}` : 'New cohort'} actions={
        <>
          <button type="button" onClick={onCancel}>Cancel</button>
          <button type="submit" className="primary" disabled={!canSave} data-testid="save-cohort">{busy ? 'Saving…' : cohort ? 'Save changes' : 'Create cohort'}</button>
        </>
      }>
        <div className="field-pair">
          <label className="field"><span>Name</span>
            <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} required placeholder="e.g. Window shoppers" aria-label="Cohort name" />
          </label>
          <label className="field"><span>Description</span>
            <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={1000} placeholder="Optional" />
          </label>
        </div>
        {!cohort && (
          <div className="kind-choice">
            <Segmented<CohortKind> label="Cohort type" value={kind} onChange={setKind}
              options={[{ id: 'dynamic', label: 'Dynamic · rules' }, { id: 'static', label: 'Static · list of ids' }]} />
            <span className="editor-hint">
              {kind === 'dynamic' ? 'Recalculated automatically; membership follows the rules.' : 'A fixed list of persons or distinct_ids, for example from a CSV export.'}
            </span>
          </div>
        )}
        <FormMessage error={error} />
      </Card>

      {kind === 'dynamic'
        ? <RulesEditor definition={definition} onChange={setDefinition} problem={problem} />
        : (
          <Card title="Members" meta={cohort ? `${num(cohort.static_members ?? 0)} ids now; paste a new list to replace them` : 'One id per line, or comma separated'}>
            <textarea className="ids-input" value={membersText} onChange={(e) => setMembersText(e.target.value)} rows={8}
              placeholder={'user-00001\nuser-00002\n01a18760-1db6-7084-affc-aab516032624'} aria-label="Member ids" />
            <div className="row">
              <label className="file-button">
                <Icon name="copy" />Load CSV
                <input type="file" accept=".csv,.txt,text/csv,text/plain" onChange={readFile} />
              </label>
              <span className="editor-hint">{num(ids.length)} unique ids · person ids include all of the person's distinct_ids</span>
            </div>
          </Card>
        )}
    </form>
  )
}

function RulesEditor({ definition, onChange, problem }: { definition: CohortDefinition; onChange: (d: CohortDefinition) => void; problem?: string }) {
  const total = definition.groups.reduce((n, g) => n + g.conditions.length, 0)
  const setGroup = (gi: number, conditions: CohortCondition[]) =>
    onChange({ groups: definition.groups.map((g, k) => (k === gi ? { conditions } : g)).filter((g) => g.conditions.length > 0) })

  return (
    <Card title="Who is in this cohort" meta={<PreviewCount definition={definition} problem={problem} />}>
      <div className="rule-groups">
        {definition.groups.map((g, gi) => (
          <div key={gi} className="rule-group-wrap">
            {gi > 0 && <div className="or-divider"><span>or</span></div>}
            <div className="rule-group" data-testid="rule-group">
              <div className="rule-group-head">
                <span className="eyebrow">{definition.groups.length > 1 ? `Group ${gi + 1} · ` : ''}Match all of</span>
                {definition.groups.length > 1 && <IconButton icon="close" label="Remove group" onClick={() => setGroup(gi, [])} />}
              </div>
              {g.conditions.map((c, ci) => (
                <ConditionRow key={ci} condition={c}
                  onChange={(next) => setGroup(gi, g.conditions.map((x, k) => (k === ci ? next : x)))}
                  onRemove={g.conditions.length > 1 || definition.groups.length > 1 ? () => setGroup(gi, g.conditions.filter((_, k) => k !== ci)) : undefined} />
              ))}
              {total < MAX_CONDITIONS && (
                <AddButton onClick={() => setGroup(gi, [...g.conditions, newCondition('performed_event')])}>And</AddButton>
              )}
            </div>
          </div>
        ))}
      </div>
      {definition.groups.length < MAX_GROUPS && total < MAX_CONDITIONS && (
        <AddButton onClick={() => onChange({ groups: [...definition.groups, { conditions: [newCondition('performed_event')] }] })}>Or another group</AddButton>
      )}
    </Card>
  )
}

function PreviewCount({ definition, problem }: { definition: CohortDefinition; problem?: string }) {
  const body = useDebounced(problem ? '' : JSON.stringify(cleanDefinition(definition)), 500)
  const preview = useQuery((signal) => (body ? api.previewCohort(JSON.parse(body), signal) : Promise.resolve(null)), [body])
  if (problem) return <span className="preview-count muted">{problem}</span>
  if (preview.error) return <span className="preview-count error">{preview.error}</span>
  if (!preview.data || preview.loading) return <span className="preview-count muted">Counting…</span>
  return <span className="preview-count" data-testid="cohort-preview"><strong>{num(preview.data.persons)}</strong> persons match right now</span>
}

function ConditionRow({ condition: c, onChange, onRemove }: {
  condition: CohortCondition; onChange: (c: CohortCondition) => void; onRemove?: () => void
}) {
  const changeType = (type: CohortCondition['type']) => {
    const next = newCondition(type)
    if (next.type !== 'person_property' && c.type !== 'person_property') onChange({ ...next, event: c.event, within_days: c.within_days } as CohortCondition)
    else onChange(next)
  }
  return (
    <div className="condition">
      <div className="row">
        <select value={c.type} onChange={(e) => changeType(e.target.value as CohortCondition['type'])} aria-label="Condition type" className="condition-type">
          {CONDITION_TYPES.map((t) => <option key={t.id} value={t.id}>{t.label}</option>)}
        </select>
        {c.type === 'person_property'
          ? <input list={propList('person')} placeholder="property" value={c.property} onChange={(e) => onChange({ ...c, property: e.target.value })} aria-label="Person property" />
          : <input list="dl-events" placeholder="event" value={c.event} onChange={(e) => onChange({ ...c, event: e.target.value })} aria-label="Event" />}
        {onRemove && <IconButton icon="close" label="Remove condition" onClick={onRemove} />}
      </div>
      {c.type === 'person_property' && (
        <div className="row">
          <OpSelect value={c.op} onChange={(op) => onChange({ ...c, op })} />
          {needsValue(c.op) && <input placeholder="value" value={c.value ?? ''} onChange={(e) => onChange({ ...c, value: e.target.value })} aria-label="Property value" />}
        </div>
      )}
      {c.type === 'performed_event' && (
        <div className="row sentence">
          <select value={c.count_op} onChange={(e) => onChange({ ...c, count_op: e.target.value as CountOp })} aria-label="Count operator" className="op">
            {COUNT_OPS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
          </select>
          <input type="number" min={1} max={10000} className="narrow" value={c.count} aria-label="Count"
            onChange={(e) => onChange({ ...c, count: Math.max(1, Math.min(10000, Number(e.target.value) || 1)) })} />
          <span>times in the last</span>
          <DaysInput value={c.within_days} onChange={(within_days) => onChange({ ...c, within_days })} />
        </div>
      )}
      {c.type === 'not_performed_event' && (
        <div className="row sentence">
          <span>in the last</span>
          <DaysInput value={c.within_days} onChange={(within_days) => onChange({ ...c, within_days })} />
        </div>
      )}
      {c.type === 'performed_event' && (
        <EventFilters filters={c.filters ?? []} onChange={(filters) => onChange({ ...c, filters })} />
      )}
    </div>
  )
}

function DaysInput({ value, onChange }: { value: number; onChange: (n: number) => void }) {
  return (
    <>
      <input type="number" min={1} max={MAX_WITHIN_DAYS} className="narrow" value={value} aria-label="Days"
        onChange={(e) => onChange(Math.max(1, Math.min(MAX_WITHIN_DAYS, Number(e.target.value) || 1)))} />
      <span>days</span>
    </>
  )
}

function EventFilters({ filters, onChange }: { filters: Filter[]; onChange: (f: Filter[]) => void }) {
  const update = (i: number, patch: Partial<Filter>) => onChange(filters.map((f, k) => (k === i ? { ...f, ...patch } : f)))
  return (
    <div className="condition-filters">
      {filters.map((f, i) => (
        <div className="row" key={i}>
          <span className="where">where</span>
          <input list={propList('event')} placeholder="event property" value={f.property} onChange={(e) => update(i, { property: e.target.value })} aria-label="Event property" />
          <OpSelect value={f.op} onChange={(op) => update(i, { op })} />
          {needsValue(f.op) && <input placeholder="value" value={f.value ?? ''} onChange={(e) => update(i, { value: e.target.value })} aria-label="Event property value" />}
          <IconButton icon="close" label="Remove event filter" onClick={() => onChange(filters.filter((_, k) => k !== i))} />
        </div>
      ))}
      {filters.length < 5 && (
        <button type="button" className="link small" onClick={() => onChange([...filters, { scope: 'event', property: '', op: 'eq', value: '' }])}>+ where…</button>
      )}
    </div>
  )
}
