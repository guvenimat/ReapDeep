import { useState } from 'react'
import type { Interval, LifecycleResponse, LifecycleStatus } from '../../api/types'
import { bucketLabel, compact, niceTicks, num } from '../../lib/format'
import { useWidth } from '../../lib/hooks'
import { columnPath, seriesColor, Tooltip, TooltipRow } from './common'

export const LIFECYCLE_STATUSES: { id: LifecycleStatus; label: string; colorIndex: number }[] = [
  { id: 'new', label: 'New', colorIndex: 0 },
  { id: 'returning', label: 'Returning', colorIndex: 1 },
  { id: 'resurrecting', label: 'Resurrecting', colorIndex: 2 },
  { id: 'dormant', label: 'Dormant', colorIndex: 3 },
]

const GAP = 2

function downPath(x: number, y: number, w: number, h: number) {
  const r = Math.min(4, w / 2, h)
  return `M${x},${y}V${y + h - r}Q${x},${y + h} ${x + r},${y + h}H${x + w - r}Q${x + w},${y + h} ${x + w},${y + h - r}V${y}Z`
}

export function LifecycleChart({ data, height = 300 }: { data: LifecycleResponse; height?: number }) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<{ i: number; x: number; y: number } | null>(null)
  const periods = data.periods
  const n = periods.length
  const up = LIFECYCLE_STATUSES.filter((s) => s.id !== 'dormant')

  const posMax = Math.max(0, ...periods.map((p) => p.new + p.returning + p.resurrecting))
  const negMax = Math.max(0, ...periods.map((p) => p.dormant))
  const base = niceTicks(Math.max(posMax, negMax, 1))
  const step = base.length > 1 ? base[1] - base[0] : 1
  const top = Math.max(step, Math.ceil(posMax / step) * step)
  const bottom = negMax > 0 ? Math.ceil(negMax / step) * step : 0
  const ticks: number[] = []
  for (let t = -bottom; t <= top; t += step) ticks.push(t)

  const m = { top: 14, right: 12, bottom: 30, left: 48 }
  const innerW = Math.max(0, width - m.left - m.right)
  const innerH = height - m.top - m.bottom
  const band = n > 0 ? innerW / n : 0
  const y = (v: number) => m.top + innerH * ((top - v) / (top + bottom))
  const colW = Math.min(36, Math.max(3, band * 0.62))
  const x = (i: number) => m.left + (i + 0.5) * band - colW / 2
  const interval = data.period as Interval
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(innerW / 72))))

  return (
    <div className="chart" ref={ref} data-testid="lifecycle-chart">
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label="Lifecycle chart: new, returning and resurrecting persons above the baseline, dormant below">
          {ticks.map((t) => (
            <g key={t}>
              {t !== 0 && <line className="grid" x1={m.left} x2={width - m.right} y1={y(t)} y2={y(t)} />}
              <text className="axis-label" x={m.left - 10} y={y(t)} dy="0.32em" textAnchor="end">{t < 0 ? `−${compact(-t)}` : compact(t)}</text>
            </g>
          ))}
          {periods.map((p, i) =>
            i % labelEvery === 0 ? (
              <text key={p.start} className="axis-label" x={m.left + (i + 0.5) * band} y={height - 8} textAnchor="middle">{bucketLabel(p.start, interval)}</text>
            ) : null,
          )}
          {hover && <rect className="hover-band" x={m.left + hover.i * band} y={m.top} width={band} height={innerH} rx={6} />}

          {periods.map((p, i) => {
            const visible = up.filter((s) => p[s.id] > 0)
            let acc = 0
            return (
              <g key={p.start} opacity={hover && hover.i !== i ? 0.55 : 1}>
                {visible.map((s, k) => {
                  const y0 = y(acc)
                  acc += p[s.id]
                  const h = Math.max(0, y0 - y(acc) - (k > 0 ? GAP : 0))
                  const yTop = y(acc)
                  const isTop = k === visible.length - 1
                  return (
                    <path key={s.id} fill={seriesColor(s.colorIndex)}
                      d={isTop ? columnPath(x(i), yTop, colW, h) : `M${x(i)},${yTop}h${colW}v${h}h${-colW}Z`} />
                  )
                })}
                {p.dormant > 0 && (
                  <path fill={seriesColor(3)} d={downPath(x(i), y(0) + GAP, colW, Math.max(0, y(-p.dormant) - y(0) - GAP))} />
                )}
              </g>
            )
          })}
          <line className="baseline" x1={m.left} x2={width - m.right} y1={y(0)} y2={y(0)} />

          <rect x={m.left} y={m.top} width={innerW} height={innerH} fill="transparent"
            onPointerMove={(e) => {
              const box = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect()
              const px = e.clientX - box.left
              const i = Math.min(n - 1, Math.max(0, Math.floor((px - m.left) / band)))
              setHover({ i, x: px, y: e.clientY - box.top })
            }}
            onPointerLeave={() => setHover(null)} />
        </svg>
      )}
      {hover && n > 0 && (
        <Tooltip x={hover.x} y={hover.y} width={width}>
          <div className="tooltip-title">{bucketLabel(periods[hover.i].start, interval, true)}</div>
          {LIFECYCLE_STATUSES.map((s) => (
            <TooltipRow key={s.id} color={seriesColor(s.colorIndex)} label={s.label}
              value={s.id === 'dormant' && periods[hover.i].dormant > 0 ? `−${num(periods[hover.i].dormant)}` : num(periods[hover.i][s.id])} />
          ))}
        </Tooltip>
      )}
    </div>
  )
}
