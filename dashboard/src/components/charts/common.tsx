import type { ReactNode } from 'react'

export const MAX_SERIES = 8
export const seriesColor = (i: number) => `var(--series-${(i % MAX_SERIES) + 1})`

export function Legend({ items }: { items: { label: string; colorIndex: number }[] }) {
  if (items.length < 2) return null
  return (
    <ul className="legend">
      {items.map((it) => (
        <li key={it.label}>
          <span className="swatch" style={{ background: seriesColor(it.colorIndex) }} />
          {it.label}
        </li>
      ))}
    </ul>
  )
}

export function Tooltip({ x, y, width, children }: { x: number; y: number; width: number; children: ReactNode }) {
  const left = x + 236 > width ? Math.max(0, x - 236) : x + 16
  return (
    <div className="tooltip" style={{ left, top: Math.max(0, y - 12) }} role="status">
      {children}
    </div>
  )
}

export function TooltipRow({ color, label, value }: { color?: string; label: ReactNode; value?: ReactNode }) {
  return (
    <div className="tooltip-row">
      {color && <span className="swatch" style={{ background: color }} />}
      <span className="tooltip-label">{label}</span>
      {value !== undefined && <span className="num">{value}</span>}
    </div>
  )
}

export function columnPath(x: number, y: number, w: number, h: number) {
  const r = Math.min(4, w / 2, h)
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`
}

export function barPath(x: number, y: number, w: number, h: number) {
  const r = Math.min(4, h / 2, w)
  return `M${x},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h - r}Q${x + w},${y + h} ${x + w - r},${y + h}H${x}Z`
}

export function monotonePath(points: [number, number][]) {
  const n = points.length
  if (n < 3) return points.map(([px, py], i) => `${i ? 'L' : 'M'}${px},${py}`).join('')
  const dx: number[] = []
  const slope: number[] = []
  for (let i = 0; i < n - 1; i++) {
    dx.push(points[i + 1][0] - points[i][0])
    slope.push((points[i + 1][1] - points[i][1]) / dx[i])
  }
  const t: number[] = [slope[0]]
  for (let i = 1; i < n - 1; i++) t.push(slope[i - 1] * slope[i] <= 0 ? 0 : (slope[i - 1] + slope[i]) / 2)
  t.push(slope[n - 2])
  for (let i = 0; i < n - 1; i++) {
    if (slope[i] === 0) {
      t[i] = 0
      t[i + 1] = 0
      continue
    }
    const a = t[i] / slope[i]
    const b = t[i + 1] / slope[i]
    const h = a * a + b * b
    if (h > 9) {
      const k = 3 / Math.sqrt(h)
      t[i] = k * a * slope[i]
      t[i + 1] = k * b * slope[i]
    }
  }
  let d = `M${points[0][0]},${points[0][1]}`
  for (let i = 0; i < n - 1; i++) {
    const [x0, y0] = points[i]
    const [x1, y1] = points[i + 1]
    const h = dx[i] / 3
    d += `C${x0 + h},${y0 + t[i] * h} ${x1 - h},${y1 - t[i + 1] * h} ${x1},${y1}`
  }
  return d
}
