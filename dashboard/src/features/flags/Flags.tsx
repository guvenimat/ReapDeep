import { useContext, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { FeatureFlag, FlagCondition, FlagOp, FlagScalar, FlagVariant } from '../../api/types'
import { CohortsContext, SuggestInput } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Badge, Card, FormMessage, IconButton, Segmented, Status } from '../../components/ui'
import { ago } from '../../lib/format'
import { useQuery } from '../../lib/hooks'

export const FLAG_OPS: { id: FlagOp; label: string }[] = [
  { id: 'eq', label: 'equals' },
  { id: 'neq', label: 'does not equal' },
  { id: 'in', label: 'is one of' },
  { id: 'not_in', label: 'is not one of' },
  { id: 'contains', label: 'contains' },
  { id: 'not_contains', label: 'does not contain' },
  { id: 'gt', label: '>' },
  { id: 'gte', label: '≥' },
  { id: 'lt', label: '<' },
  { id: 'lte', label: '≤' },
  { id: 'is_set', label: 'is set' },
  { id: 'is_not_set', label: 'is not set' },
  { id: 'in_cohort', label: 'is in cohort' },
  { id: 'not_in_cohort', label: 'is not in cohort' },
]

const isList = (op: FlagOp) => op === 'in' || op === 'not_in'
const isNumeric = (op: FlagOp) => op === 'gt' || op === 'gte' || op === 'lt' || op === 'lte'
const hasValue = (op: FlagOp) => op !== 'is_set' && op !== 'is_not_set'
const isCohort = (op: FlagOp) => op === 'in_cohort' || op === 'not_in_cohort'

export function describeCondition(c: FlagCondition, cohortName?: (id: number) => string | undefined) {
  const op = FLAG_OPS.find((o) => o.id === c.op)?.label ?? c.op
  if (isCohort(c.op)) return `person ${op} ${cohortName?.(Number(c.value)) ?? `#${c.value}`}`
  if (!hasValue(c.op)) return `${c.property} ${op}`
  const value = Array.isArray(c.value) ? c.value.join(', ') : String(c.value ?? '')
  return `${c.property} ${op} ${value}`
}

export function Flags({ canManage }: { canManage: boolean }) {
  const cohorts = useContext(CohortsContext)
  const cohortName = (id: number) => cohorts.find((c) => c.id === id)?.name
  const [version, setVersion] = useState(0)
  const [editing, setEditing] = useState<FeatureFlag | 'new'>()
  const [message, setMessage] = useState<{ text: string; error?: boolean }>()
  const list = useQuery((signal) => api.flags(signal), [version])
  const refresh = () => setVersion((v) => v + 1)

  const run = async (fn: () => Promise<string>) => {
    setMessage(undefined)
    try {
      setMessage({ text: await fn() })
      refresh()
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    }
  }

  const flags = list.data?.flags ?? []

  return (
    <div className="stack">
      {editing && (
        <FlagEditor flag={editing === 'new' ? undefined : editing} onCancel={() => setEditing(undefined)}
          onSaved={(f, created) => {
            setEditing(undefined)
            setMessage({ text: created ? `Created "${f.key}"` : `Saved "${f.key}"` })
            refresh()
          }} />
      )}
      <Card testId="flags-card" title="Feature flags" meta="Evaluated by the Recommender's /flags endpoint; the SDK reads them with loadFlags() and getFlag()"
        actions={canManage && !editing ? <button type="button" className="primary" onClick={() => setEditing('new')} data-testid="new-flag">New flag</button> : undefined}>
        <FormMessage error={message?.error ? message.text : undefined} success={message?.error ? undefined : message?.text} />
        <Status state={list} empty={flags.length === 0} emptyTitle="No feature flags yet"
          emptyText="Create a flag to release a feature to a share of visitors, or to split them into variants.">
          <div className="table-wrap">
            <table data-testid="flags-table">
              <thead>
                <tr><th>Flag</th><th>Status</th><th className="num">Rollout</th><th>Variants</th><th>Targeting</th><th>Updated</th>
                  {canManage && <th><span className="sr-only">Actions</span></th>}</tr>
              </thead>
              <tbody>
                {flags.map((f) => (
                  <tr key={f.id} className="flag-row" data-flag-key={f.key}>
                    <td>
                      <code className="chip">{f.key}</code>
                      {f.name !== f.key && <span className="strong flag-name">{f.name}</span>}
                      {f.description && <span className="cell-sub">{f.description}</span>}
                    </td>
                    <td>
                      {canManage ? (
                        <label className="switch flag-toggle">
                          <input type="checkbox" checked={f.enabled} aria-label={`${f.enabled ? 'Disable' : 'Enable'} ${f.key}`}
                            onChange={() => run(async () => {
                              await api.updateFlag(f.id, { enabled: !f.enabled })
                              return `${f.key} is ${f.enabled ? 'off' : 'on'}`
                            })} />
                          <span className="switch-track" aria-hidden />
                          <Badge tone={f.enabled ? 'good' : 'muted'}>{f.enabled ? 'On' : 'Off'}</Badge>
                        </label>
                      ) : <Badge tone={f.enabled ? 'good' : 'muted'}>{f.enabled ? 'On' : 'Off'}</Badge>}
                    </td>
                    <td className="num">{f.rollout_percentage}%</td>
                    <td>
                      {f.variants.length === 0
                        ? <span className="muted">On / off</span>
                        : <span className="path-chips">{f.variants.map((v) => <span key={v.key} className="chip">{v.key} {v.percentage}%</span>)}</span>}
                      {f.locked_by_experiment && <span className="cell-sub flag-lock" title={`Locked by experiment ${f.locked_by_experiment}`}>Locked by {f.locked_by_experiment}</span>}
                    </td>
                    <td className="flag-targeting">
                      {f.conditions.length === 0
                        ? <span className="muted">Everyone</span>
                        : f.conditions.map((c, i) => <span key={i} className="cell-sub">{describeCondition(c, cohortName)}</span>)}
                    </td>
                    <td className="nowrap muted" title={f.updated_by ?? undefined}>{ago(f.updated_at)}</td>
                    {canManage && (
                      <td className="actions">
                        <IconButton icon="edit" label={`Edit ${f.key}`} onClick={() => setEditing(f)} />
                        <IconButton icon="trash" label={`Delete ${f.key}`} tone="danger" onClick={() => {
                          if (window.confirm(`Delete the flag "${f.key}"? Sites that read it get undefined from getFlag().`))
                            void run(async () => { await api.deleteFlag(f.id); return `Deleted "${f.key}"` })
                        }} />
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Status>
      </Card>
    </div>
  )
}

interface ConditionDraft { property: string; op: FlagOp; value: string }

function toDraft(c: FlagCondition): ConditionDraft {
  return { property: c.property, op: c.op, value: Array.isArray(c.value) ? c.value.join(', ') : c.value == null ? '' : String(c.value) }
}

function fromDraft(c: ConditionDraft): FlagCondition {
  const property = c.property.trim()
  if (isCohort(c.op)) return { property: '$cohort', op: c.op, value: Number(c.value) }
  if (!hasValue(c.op)) return { property, op: c.op }
  if (isList(c.op)) return { property, op: c.op, value: c.value.split(',').map((v) => v.trim()).filter(Boolean) }
  const value: FlagScalar = isNumeric(c.op) && c.value.trim() !== '' && !Number.isNaN(Number(c.value)) ? Number(c.value) : c.value
  return { property, op: c.op, value }
}

const DEFAULT_VARIANTS: FlagVariant[] = [{ key: 'control', percentage: 50 }, { key: 'test', percentage: 50 }]

function FlagEditor({ flag, onSaved, onCancel }: { flag?: FeatureFlag; onSaved: (f: FeatureFlag, created: boolean) => void; onCancel: () => void }) {
  const [key, setKey] = useState(flag?.key ?? '')
  const [name, setName] = useState(flag?.name ?? '')
  const [description, setDescription] = useState(flag?.description ?? '')
  const [rollout, setRollout] = useState(String(flag?.rollout_percentage ?? 100))
  const [kind, setKind] = useState<'boolean' | 'multivariate'>(flag?.variants.length ? 'multivariate' : 'boolean')
  const [variants, setVariants] = useState<FlagVariant[]>(flag?.variants.length ? flag.variants : DEFAULT_VARIANTS)
  const [conditions, setConditions] = useState<ConditionDraft[]>((flag?.conditions ?? []).map(toDraft))
  const cohorts = useContext(CohortsContext)
  const [enabled, setEnabled] = useState(flag?.enabled ?? false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const lockedBy = flag?.locked_by_experiment ?? null
  const total = variants.reduce((s, v) => s + (Number(v.percentage) || 0), 0)
  const valid = key.trim() !== '' && rollout !== '' && (kind === 'boolean' || total === 100) && conditions.every((c) => (isCohort(c.op) ? c.value !== '' : c.property.trim() !== ''))

  const setVariant = (i: number, patch: Partial<FlagVariant>) => setVariants(variants.map((v, j) => (j === i ? { ...v, ...patch } : v)))
  const setCondition = (i: number, patch: Partial<ConditionDraft>) => setConditions(conditions.map((c, j) => (j === i ? { ...c, ...patch } : c)))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      const body = {
        name: name.trim(), description: description.trim(), enabled, rollout_percentage: Number(rollout),
        variants: kind === 'multivariate' ? variants.map((v) => ({ key: v.key.trim(), percentage: Number(v.percentage) })) : [],
        conditions: conditions.map(fromDraft),
      }
      onSaved(flag ? await api.updateFlag(flag.id, body) : await api.createFlag({ ...body, key: key.trim() }), !flag)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} data-testid="flag-editor">
      <Card title={flag ? `Edit ${flag.key}` : 'New feature flag'} actions={<>
        <button type="button" onClick={onCancel}>Cancel</button>
        <button type="submit" className="primary" disabled={busy || !valid}>{busy ? 'Saving…' : flag ? 'Save changes' : 'Create flag'}</button>
      </>}>
        <div className="flag-editor">
          <div className="field-pair">
            <label className="field" htmlFor="flag-key"><span>Key</span>
              <input id="flag-key" value={key} onChange={(e) => setKey(e.target.value)} maxLength={100} placeholder="new-checkout" disabled={!!flag}
                pattern="[A-Za-z0-9][A-Za-z0-9_\-]*" title="Letters, digits, '-' and '_'" />
            </label>
            <label className="field" htmlFor="flag-name"><span>Name</span>
              <input id="flag-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder="New checkout flow" />
            </label>
          </div>
          <label className="field" htmlFor="flag-description"><span>Description</span>
            <input id="flag-description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={1000} placeholder="What the flag changes and who owns it" />
          </label>

          <div className="editor-block">
            <span className="editor-title">Who gets the flag</span>
            <span className="editor-hint">All conditions must match the person properties the site passes to loadFlags(). With no conditions everyone qualifies.</span>
            {conditions.map((c, i) => (
              <div className="row flag-condition" key={i}>
                {isCohort(c.op)
                  ? <span className="cohort-subject">Person</span>
                  : <SuggestInput list="person-props" placeholder="property" value={c.property} onChange={(v) => setCondition(i, { property: v })} aria-label="Person property" />}
                <select value={c.op} aria-label="Operator" className="op" onChange={(e) => {
                  const op = e.target.value as FlagOp
                  setCondition(i, isCohort(op) === isCohort(c.op) ? { op } : { op, property: isCohort(op) ? '$cohort' : '', value: '' })
                }}>
                  {FLAG_OPS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
                </select>
                {isCohort(c.op) && (
                  <select value={c.value} onChange={(e) => setCondition(i, { value: e.target.value })} aria-label="Cohort">
                    <option value="">Choose a cohort…</option>
                    {cohorts.map((co) => <option key={co.id} value={String(co.id)}>{co.name}</option>)}
                  </select>
                )}
                {hasValue(c.op) && !isCohort(c.op) && (
                  <input value={c.value} onChange={(e) => setCondition(i, { value: e.target.value })} aria-label="Value"
                    placeholder={isList(c.op) ? 'pro, team' : isNumeric(c.op) ? '18' : 'value'} />
                )}
                <IconButton icon="close" label="Remove condition" onClick={() => setConditions(conditions.filter((_, j) => j !== i))} />
              </div>
            ))}
            <button type="button" className="add-button" onClick={() => setConditions([...conditions, { property: '', op: 'eq', value: '' }])}>+ Add condition</button>
          </div>

          <div className="row sentence">
            <span>Roll out to</span>
            <input type="number" min={0} max={100} step={1} className="narrow" value={rollout} onChange={(e) => setRollout(e.target.value)} aria-label="Rollout percentage" />
            <span>% of the visitors who match</span>
          </div>

          <div className="editor-block">
            {lockedBy && (
              <div className="notice warn" role="status" data-testid="flag-locked">
                <Icon name="flask" />
                <span><strong>Locked by experiment {lockedBy}.</strong> Variant keys and percentages cannot change while it runs; stop the experiment to edit them. On/off, rollout and targeting can still change.</span>
              </div>
            )}
            {lockedBy
              ? <span className="muted small">{kind === 'multivariate' ? 'Variants' : 'On / off'}</span>
              : <Segmented label="Flag type" value={kind} onChange={setKind}
                options={[{ id: 'boolean', label: 'On / off' }, { id: 'multivariate', label: 'Variants' }]} />}
            {kind === 'multivariate' && (
              <>
                {variants.map((v, i) => (
                  <div className="row" key={i}>
                    <input value={v.key} onChange={(e) => setVariant(i, { key: e.target.value })} maxLength={50} aria-label="Variant key" placeholder="variant" disabled={!!lockedBy} />
                    <input type="number" min={0} max={100} className="narrow" value={v.percentage} aria-label="Variant percentage" disabled={!!lockedBy}
                      onChange={(e) => setVariant(i, { percentage: e.target.value === '' ? 0 : Number(e.target.value) })} />
                    <span>%</span>
                    {variants.length > 2 && !lockedBy && <IconButton icon="close" label="Remove variant" onClick={() => setVariants(variants.filter((_, j) => j !== i))} />}
                  </div>
                ))}
                <div className="row">
                  {variants.length < 10 && !lockedBy && (
                    <button type="button" className="add-button" onClick={() => setVariants([...variants, { key: `variant-${variants.length + 1}`, percentage: 0 }])}>+ Add variant</button>
                  )}
                  <span className={total === 100 ? 'muted small' : 'form-message error'}>Total {total}%{total === 100 ? '' : ', must be 100%'}</span>
                </div>
              </>
            )}
          </div>

          <label className="switch">
            <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
            <span className="switch-track" aria-hidden />
            Enabled
          </label>
          <p className="footnote">A visitor's share is fixed by hashing the flag key with the SDK's first anonymous id, so the same browser keeps its variant, also after it signs in. Changes reach the Recommender within a few seconds. A disabled flag is left out of the /flags response, so getFlag() returns undefined.</p>
          <FormMessage error={error} />
        </div>
      </Card>
    </form>
  )
}
