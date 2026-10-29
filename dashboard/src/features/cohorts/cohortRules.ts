import type { CohortCondition, CohortDefinition, CountOp, Op } from '../../api/types'

export const MAX_GROUPS = 5
export const MAX_CONDITIONS = 10
export const MAX_WITHIN_DAYS = 90

export const COUNT_OPS: { id: CountOp; label: string }[] = [
  { id: 'gte', label: 'at least' },
  { id: 'lte', label: 'at most' },
  { id: 'eq', label: 'exactly' },
]

const OP_TEXT: Partial<Record<Op, string>> = { eq: 'is', neq: 'is not', contains: 'contains', is_set: 'is set' }

export const newCondition = (type: CohortCondition['type']): CohortCondition =>
  type === 'performed_event'
    ? { type, event: '', count_op: 'gte', count: 1, within_days: 30 }
    : type === 'not_performed_event'
      ? { type, event: '', within_days: 30 }
      : { type, property: '', op: 'eq', value: '' }

export const emptyDefinition = (): CohortDefinition => ({ groups: [{ conditions: [newCondition('performed_event')] }] })

const conditionComplete = (c: CohortCondition) => {
  if (c.type === 'person_property') return c.property.trim() !== '' && (c.op === 'is_set' || (c.value ?? '').trim() !== '')
  const days = c.within_days >= 1 && c.within_days <= MAX_WITHIN_DAYS
  return c.event.trim() !== '' && days && (c.type === 'not_performed_event' || c.count >= 1)
}

const hasPositive = (conditions: CohortCondition[]) => conditions.some((c) => c.type !== 'not_performed_event')

export function definitionProblem(def: CohortDefinition): string | undefined {
  if (def.groups.length === 0) return 'Add at least one group'
  if (def.groups.reduce((n, g) => n + g.conditions.length, 0) > MAX_CONDITIONS) return `At most ${MAX_CONDITIONS} conditions`
  if (def.groups.some((g) => g.conditions.length === 0)) return 'Every group needs a condition'
  if (def.groups.some((g) => !hasPositive(g.conditions))) return 'Each group needs a "did" or person property condition, not only "did not"'
  if (def.groups.some((g) => g.conditions.some((c) => !conditionComplete(c)))) return 'Fill in every condition'
  return undefined
}

export function cleanDefinition(def: CohortDefinition): CohortDefinition {
  return {
    groups: def.groups.map((g) => ({
      conditions: g.conditions.map((c) => {
        if (c.type === 'person_property') return { ...c, property: c.property.trim(), value: c.op === 'is_set' ? undefined : c.value?.trim() }
        if (c.type === 'not_performed_event') return { ...c, event: c.event.trim() }
        const filters = (c.filters ?? []).filter((f) => f.property.trim() && (f.op === 'is_set' || (f.value ?? '').trim() !== ''))
        return { ...c, event: c.event.trim(), filters: filters.length ? filters : undefined }
      }),
    })),
  }
}

const times = (n: number) => `${n} time${n === 1 ? '' : 's'}`
const days = (n: number) => `${n} day${n === 1 ? '' : 's'}`

export function describeCondition(c: CohortCondition): string {
  if (c.type === 'person_property') return `${c.property} ${OP_TEXT[c.op] ?? c.op}${c.op === 'is_set' ? '' : ` "${c.value ?? ''}"`}`
  if (c.type === 'not_performed_event') return `did not do ${c.event} in the last ${days(c.within_days)}`
  const label = COUNT_OPS.find((o) => o.id === c.count_op)?.label ?? 'at least'
  const where = (c.filters ?? []).map((f) => ` where ${f.property} ${OP_TEXT[f.op] ?? f.op}${f.op === 'is_set' ? '' : ` "${f.value ?? ''}"`}`).join('')
  return `did ${c.event} ${label} ${times(c.count)} in the last ${days(c.within_days)}${where}`
}

export function parseIds(text: string): string[] {
  return [...new Set(text.split(/[\n\r,;\t]+/).map((s) => s.trim().replace(/^"|"$/g, '')).filter(Boolean))]
}
