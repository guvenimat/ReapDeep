import { useId, useState } from 'react'
import type { Interval } from '../../api/types'
import { bucketLabel, compact, niceTicks, num } from '../../lib/format'
import { useWidth } from '../../lib/hooks'
import { columnPath, monotonePath, seriesColor, Tooltip, TooltipRow } from './common'

export interface ChartSeries { key: string; label: string; colorIndex: number; values: number[]; dashed?: boolean }

export function TimeChart({ buckets, interval, series, mode, height = 280 }: {
  buckets: string[]; interval: Interval; series: ChartSeries[]; mode: 'line' | 'bar'; height?: number
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<{ i: number; x: number; y: number } | null>(null)
  const gradientId = useId().replace(/:/g, '')

  const directLabels = mode === 'line' && series.length <= 4 && !series.some((s) => s.dashed)
  const m = { top: 14, right: directLabels ? 52 : 12, bottom: 30, left: 44 }
  const innerW = Math.max(0, width - m.left - m.right)
  const innerH = height - m.top - m.bottom
  const n = buckets.length
  const band = n > 0 ? innerW / n : 0
  const ticks = niceTicks(Math.max(0, ...series.flatMap((s) => s.values)))
  const yMax = ticks[ticks.length - 1] || 1
  const x = (i: number) => m.left + (i + 0.5) * band
  const y = (v: number) => m.top + innerH * (1 - v / yMax)
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(innerW / 72))))
  const showArea = mode === 'line' && series.length <= 2 && n > 1 && !series.some((s) => s.dashed)

  let endLabels: { s: ChartSeries; y: number }[] = []
  if (directLabels && n > 0) {
    endLabels = series.map((s) => ({ s, y: y(s.values[n - 1]) })).sort((a, b) => a.y - b.y)
    if (endLabels.some((l, k) => k > 0 && l.y - endLabels[k - 1].y < 14)) endLabels = []
  }

  const colW = Math.min(22, Math.max(2, (band * 0.68 - 2 * (series.length - 1)) / Math.max(1, series.length)))
  const groupW = colW * series.length + 2 * (series.length - 1)

  return (
    <div className="chart" ref={ref}>
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label="Time series chart">
          {showArea && (
            <defs>
              {series.map((s) => (
                <linearGradient key={s.key} id={`${gradientId}-${s.key}`} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stopColor={seriesColor(s.colorIndex)} stopOpacity={0.16} />
                  <stop offset="1" stopColor={seriesColor(s.colorIndex)} stopOpacity={0} />
                </linearGradient>
              ))}
            </defs>
          )}
          {ticks.map((t) => (
            <g key={t}>
              {t > 0 && <line className="grid" x1={m.left} x2={width - m.right} y1={y(t)} y2={y(t)} />}
              <text className="axis-label" x={m.left - 10} y={y(t)} dy="0.32em" textAnchor="end">{compact(t)}</text>
            </g>
          ))}
          <line className="baseline" x1={m.left} x2={width - m.right} y1={y(0)} y2={y(0)} />
          {buckets.map((b, i) =>
            i % labelEvery === 0 ? (
              <text key={b} className="axis-label" x={x(i)} y={height - 8} textAnchor="middle">{bucketLabel(b, interval)}</text>
            ) : null,
          )}

          {hover && mode === 'bar' && <rect className="hover-band" x={m.left + hover.i * band} y={m.top} width={band} height={innerH} rx={6} />}
          {hover && mode === 'line' && <line className="crosshair" x1={x(hover.i)} x2={x(hover.i)} y1={m.top} y2={m.top + innerH} />}

          {mode === 'line'
            ? series.map((s) => {
                const pts = s.values.map((v, i) => [x(i), y(v)] as [number, number])
                const d = monotonePath(pts)
                return (
                  <g key={s.key}>
                    {showArea && <path className="area" fill={`url(#${gradientId}-${s.key})`} d={`${d}L${x(n - 1)},${y(0)}L${x(0)},${y(0)}Z`} />}
                    {n > 1 && <path className={s.dashed ? 'line dashed' : 'line'} stroke={seriesColor(s.colorIndex)} d={d} />}
                    {n === 1 && <circle className="dot" cx={x(0)} cy={y(s.values[0])} r={4} fill={seriesColor(s.colorIndex)} />}
                  </g>
                )
              })
            : series.map((s, k) =>
                s.values.map((v, i) =>
                  v > 0 ? (
                    <path key={`${s.key}-${i}`} fill={seriesColor(s.colorIndex)}
                      d={columnPath(x(i) - groupW / 2 + k * (colW + 2), y(v), colW, y(0) - y(v))}
                      opacity={(hover && hover.i !== i ? 0.55 : 1) * (s.dashed ? 0.45 : 1)} />
                  ) : null,
                ),
              )}

          {endLabels.map(({ s, y: ly }) => (
            <g key={s.key}>
              <circle cx={x(n - 1)} cy={ly} r={4} fill={seriesColor(s.colorIndex)} className="dot" />
              <text className="end-label" x={x(n - 1) + 10} y={ly} dy="0.32em">{compact(s.values[n - 1])}</text>
            </g>
          ))}

          {hover && mode === 'line' && series.map((s) => (
            <circle key={s.key} className="dot hover-dot" cx={x(hover.i)} cy={y(s.values[hover.i])} r={4.5} fill={seriesColor(s.colorIndex)} />
          ))}

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
          <div className="tooltip-title">{bucketLabel(buckets[hover.i], interval, true)}</div>
          {[...series].sort((a, b) => b.values[hover.i] - a.values[hover.i]).map((s) => (
            <TooltipRow key={s.key} color={seriesColor(s.colorIndex)} label={s.label} value={num(s.values[hover.i])} />
          ))}
        </Tooltip>
      )}
    </div>
  )
}
