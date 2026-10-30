import type { Interval } from '../api/types'

const nf = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 })
const compactNf = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 })

export const num = (n: number) => nf.format(n)
export const ms = (n: number) => `${nf.format(Math.round(n))} ms`
export const compact = (n: number) => (Math.abs(n) >= 1000 ? compactNf.format(n) : nf.format(n))
export const pct = (r: number) => `${(r * 100).toLocaleString('en-US', { maximumFractionDigits: 1 })}%`

export function bucketLabel(bucket: string, interval: Interval, long = false): string {
  const [date, time] = bucket.split('T')
  const [y, m, d] = date.split('-')
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
  const mon = months[Number(m) - 1]
  switch (interval) {
    case 'hour':
      return long ? `${Number(d)} ${mon} ${time.slice(0, 5)}` : time.startsWith('00:00') ? `${Number(d)} ${mon}` : time.slice(0, 5)
    case 'month':
      return `${mon} ${y}`
    case 'week':
      return long ? `Week of ${mon} ${Number(d)}` : `${Number(d)} ${mon}`
    default:
      return long ? `${Number(d)} ${mon} ${y}` : `${Number(d)} ${mon}`
  }
}

export const dateTime = (iso: string) =>
  new Date(iso).toLocaleString('en-US', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit' })

export function niceTicks(max: number, count = 4): number[] {
  if (max <= 0) return [0, 1]
  const raw = max / count
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 2.5, 5, 10].map((s) => s * mag).find((s) => s >= raw) ?? raw
  const ticks: number[] = []
  for (let v = 0; v <= max + step * 0.001; v += step) ticks.push(Math.round(v * 1e6) / 1e6)
  if (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step)
  return ticks
}

export const shortId = (id: string) => (id.length > 18 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id)

export const initials = (text: string) => {
  const local = text.split('@')[0]
  const words = local.split(/[\s._-]+/).filter((w) => /^\p{L}/u.test(w))
  return ((words[0]?.[0] ?? local[0] ?? '?') + (words[1]?.[0] ?? '')).toUpperCase()
}

export function ago(iso: string | null): string {
  if (!iso) return 'never'
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000))
  if (s < 45) return 'just now'
  if (s < 3600) return `${Math.round(s / 60)} min ago`
  if (s < 86400) return `${Math.round(s / 3600)} h ago`
  return `${Math.round(s / 86400)} d ago`
}
