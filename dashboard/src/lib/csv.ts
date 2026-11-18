import type { FunnelResponse, LifecycleResponse, PathsResponse, RetentionResponse, RfmResponse, TrendResponse } from '../api/types'

export type Cell = string | number | null | undefined
export interface CsvTable { name: string; headers: string[]; rows: Cell[][] }

const FORMULA_START = /^[=+\-@\t\r]/

function cell(value: Cell): string {
  if (value === null || value === undefined) return ''
  let text = typeof value === 'number' ? (Number.isFinite(value) ? String(value) : '') : value
  if (typeof value === 'string' && FORMULA_START.test(text)) text = `'${text}`
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

export function toCsv(table: CsvTable): string {
  return [table.headers, ...table.rows].map((row) => row.map(cell).join(',')).join('\r\n') + '\r\n'
}

export function downloadCsv(table: CsvTable): void {
  const blob = new Blob(['﻿', toCsv(table)], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `${table.name.replace(/[^\w.-]+/g, '-').replace(/^-+|-+$/g, '') || 'export'}.csv`
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export function trendsCsv(data: TrendResponse): CsvTable {
  const buckets = [...new Set(data.results.flatMap((r) => r.data.map((d) => d.bucket)))].sort()
  const label = (r: TrendResponse['results'][number]) =>
    [r.series ?? r.event, r.metric, r.property, r.breakdown_value, r.compare ? 'previous period' : null].filter((p) => p !== null && p !== '').join(' · ')
  return {
    name: 'trends',
    headers: ['bucket', ...data.results.map(label)],
    rows: buckets.map((b) => [b, ...data.results.map((r) => r.data.find((d) => d.bucket === b)?.count ?? null)]),
  }
}

export function funnelsCsv(data: FunnelResponse): CsvTable {
  return {
    name: 'funnel',
    headers: ['breakdown', 'step', 'event', 'persons', 'conversion_from_first', 'conversion_from_previous'],
    rows: data.results.flatMap((r) => r.steps.map((s) => [r.breakdown_value ?? '', s.step, s.event, s.count, s.conversion_from_first, s.conversion_from_previous])),
  }
}

export function retentionCsv(data: RetentionResponse): CsvTable {
  const periods = Array.from({ length: data.periods }, (_, i) => `${data.period}_${i}`)
  return {
    name: 'retention',
    headers: ['cohort_start', 'size', ...periods],
    rows: [
      ...data.cohorts.map((c) => [c.cohort_start, c.size, ...periods.map((_, i) => c.counts[i] ?? null)]),
      ['average', null, ...periods.map((_, i) => data.average[i] ?? null)],
    ],
  }
}

export function pathsCsv(data: PathsResponse): CsvTable {
  const depth = Math.max(0, ...data.paths.map((p) => p.nodes.length))
  return {
    name: 'paths',
    headers: [...Array.from({ length: depth }, (_, i) => `step_${i + 1}`), 'persons'],
    rows: data.paths.map((p) => [...Array.from({ length: depth }, (_, i) => p.nodes[i] ?? null), p.persons]),
  }
}

export function lifecycleCsv(data: LifecycleResponse): CsvTable {
  return {
    name: 'lifecycle',
    headers: ['period_start', 'new', 'returning', 'resurrecting', 'dormant'],
    rows: data.periods.map((p) => [p.start, p.new, p.returning, p.resurrecting, p.dormant]),
  }
}

export function rfmCsv(data: RfmResponse): CsvTable {
  return {
    name: 'rfm-segments',
    headers: ['segment', 'persons', 'share', 'avg_recency_days', 'avg_frequency', 'avg_monetary', 'revenue', 'revenue_share'],
    rows: data.segments.map((s) => [s.name, s.persons, s.share, s.avg_recency_days, s.avg_frequency, s.avg_monetary, s.revenue, s.revenue_share]),
  }
}
