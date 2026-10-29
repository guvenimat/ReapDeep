import { useCallback, useEffect, useState } from 'react'
import type { DateRange, Interval } from './api'

export type Tab = 'trends' | 'funnels' | 'events' | 'persons' | 'settings'
export const TABS: { id: Tab; label: string }[] = [
  { id: 'trends', label: 'Trends' },
  { id: 'funnels', label: 'Funnels' },
  { id: 'events', label: 'Events' },
  { id: 'persons', label: 'Persons' },
  { id: 'settings', label: 'Settings' },
]

export interface Route { tab: Tab; id?: string; params: URLSearchParams }

function parse(): Route {
  const [path, query = ''] = location.hash.replace(/^#\/?/, '').split('?')
  const [tab, id] = path.split('/')
  return {
    tab: TABS.some((t) => t.id === tab) ? (tab as Tab) : 'trends',
    id: id ? decodeURIComponent(id) : undefined,
    params: new URLSearchParams(query),
  }
}

function build(r: Route) {
  const q = r.params.toString()
  return `#/${r.tab}${r.id ? '/' + encodeURIComponent(r.id) : ''}${q ? '?' + q : ''}`
}

export function useRoute() {
  const [route, setRoute] = useState(parse)
  useEffect(() => {
    const on = () => setRoute(parse())
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])

  const navigate = useCallback((tab: Tab, id?: string) => {
    const keep = new URLSearchParams()
    for (const k of ['range', 'from', 'to']) {
      const v = parse().params.get(k)
      if (v) keep.set(k, v)
    }
    location.hash = build({ tab, id, params: keep })
  }, [])

  const setParams = useCallback((patch: Record<string, string | undefined>) => {
    const r = parse()
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined || v === '') r.params.delete(k)
      else r.params.set(k, v)
    }
    history.replaceState(null, '', build(r))
    setRoute(r)
  }, [])

  return { route, navigate, setParams }
}

export function readState<T>(params: URLSearchParams, fallback: T): T {
  const raw = params.get('q')
  if (!raw) return fallback
  try {
    return { ...fallback, ...(JSON.parse(raw) as Partial<T>) }
  } catch {
    return fallback
  }
}

export type RangePreset = '24h' | '7d' | '30d' | 'custom'
export const RANGE_PRESETS: { id: RangePreset; label: string }[] = [
  { id: '24h', label: 'Last 24 hours' },
  { id: '7d', label: 'Last 7 days' },
  { id: '30d', label: 'Last 30 days' },
  { id: 'custom', label: 'Custom' },
]

const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

export function rangeFromParams(params: URLSearchParams): { preset: RangePreset; from?: string; to?: string } {
  const preset = (params.get('range') as RangePreset) || '7d'
  return { preset: RANGE_PRESETS.some((p) => p.id === preset) ? preset : '7d', from: params.get('from') ?? undefined, to: params.get('to') ?? undefined }
}

export function toDateRange(r: ReturnType<typeof rangeFromParams>): DateRange {
  const now = new Date()
  switch (r.preset) {
    case '24h':
      return { date_from: new Date(now.getTime() - 24 * 3600_000).toISOString() }
    case '30d':
      return { date_from: ymd(new Date(now.getTime() - 29 * 86400_000)), date_to: ymd(now) }
    case 'custom':
      return { date_from: r.from, date_to: r.to }
    default:
      return { date_from: ymd(new Date(now.getTime() - 6 * 86400_000)), date_to: ymd(now) }
  }
}

export const defaultInterval = (preset: RangePreset): Interval => (preset === '24h' ? 'hour' : 'day')
