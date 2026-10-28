import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import type { Interval } from './api'
import { bucketLabel, compact, niceTicks, num, pct } from './format'

export const MAX_SERIES = 8
export const seriesColor = (i: number) => `var(--series-${(i % MAX_SERIES) + 1})`

function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const ro = new ResizeObserver(([entry]) => setWidth(Math.floor(entry.contentRect.width)))
    ro.observe(el)
    setWidth(el.clientWidth)
    return () => ro.disconnect()
  }, [])
  return [ref, width] as const
}

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

function Tooltip({ x, y, width, children }: { x: number; y: number; width: number; children: ReactNode }) {
  const left = x + 220 > width ? Math.max(0, x - 220) : x + 14
  return (
    <div className="tooltip" style={{ left, top: Math.max(0, y - 10) }} role="status">
      {children}
    </div>
  )
}

function columnPath(x: number, y: number, w: number, h: number) {
  const r = Math.min(4, w / 2, h)
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`
}

function barPath(x: number, y: number, w: number, h: number) {
  const r = Math.min(4, h / 2, w)
  return `M${x},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h - r}Q${x + w},${y + h} ${x + w - r},${y + h}H${x}Z`
}

export interface ChartSeries { key: string; label: string; colorIndex: number; values: number[] }

export function TimeChart({ buckets, interval, series, mode }: { buckets: string[]; interval: Interval; series: ChartSeries[]; mode: 'line' | 'bar' }) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<{ i: number; x: number; y: number } | null>(null)

  const height = 260
  const directLabels = mode === 'line' && series.length <= 4
  const m = { top: 12, right: directLabels ? 56 : 16, bottom: 28, left: 48 }
  const innerW = Math.max(0, width - m.left - m.right)
  const innerH = height - m.top - m.bottom
  const n = buckets.length
  const band = n > 0 ? innerW / n : 0
  const ticks = niceTicks(Math.max(0, ...series.flatMap((s) => s.values)))
  const yMax = ticks[ticks.length - 1] || 1
  const x = (i: number) => m.left + (i + 0.5) * band
  const y = (v: number) => m.top + innerH * (1 - v / yMax)

  const labelEvery = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(innerW / 70))))

  let endLabels: { s: ChartSeries; y: number }[] = []
  if (directLabels && n > 0) {
    endLabels = series.map((s) => ({ s, y: y(s.values[n - 1]) })).sort((a, b) => a.y - b.y)
    if (endLabels.some((l, k) => k > 0 && l.y - endLabels[k - 1].y < 14)) endLabels = []
  }

  const colW = Math.min(24, Math.max(2, (band * 0.7 - 2 * (series.length - 1)) / Math.max(1, series.length)))
  const groupW = colW * series.length + 2 * (series.length - 1)

  return (
    <div className="chart" ref={ref}>
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label="Time series chart">
          {ticks.map((t) => (
            <g key={t}>
              <line className="grid" x1={m.left} x2={width - m.right} y1={y(t)} y2={y(t)} />
              <text className="axis-label" x={m.left - 8} y={y(t)} dy="0.32em" textAnchor="end">{compact(t)}</text>
            </g>
          ))}
          <line className="baseline" x1={m.left} x2={width - m.right} y1={y(0)} y2={y(0)} />
          {buckets.map((b, i) =>
            i % labelEvery === 0 ? (
              <text key={b} className="axis-label" x={x(i)} y={height - 8} textAnchor="middle">{bucketLabel(b, interval)}</text>
            ) : null,
          )}

          {hover && mode === 'bar' && <rect className="hover-band" x={m.left + hover.i * band} y={m.top} width={band} height={innerH} />}

          {mode === 'line'
            ? series.map((s) => (
                <g key={s.key}>
                  {n > 1 && (
                    <path className="line" stroke={seriesColor(s.colorIndex)}
                      d={s.values.map((v, i) => `${i ? 'L' : 'M'}${x(i)},${y(v)}`).join('')} />
                  )}
                  {n === 1 && <circle className="dot" cx={x(0)} cy={y(s.values[0])} r={4} fill={seriesColor(s.colorIndex)} />}
                </g>
              ))
            : series.map((s, k) =>
                s.values.map((v, i) =>
                  v > 0 ? (
                    <path key={`${s.key}-${i}`} fill={seriesColor(s.colorIndex)}
                      d={columnPath(x(i) - groupW / 2 + k * (colW + 2), y(v), colW, y(0) - y(v))} />
                  ) : null,
                ),
              )}

          {endLabels.map(({ s, y: ly }) => (
            <g key={s.key}>
              <circle cx={x(n - 1)} cy={ly} r={4} fill={seriesColor(s.colorIndex)} className="dot" />
              <text className="end-label" x={x(n - 1) + 10} y={ly} dy="0.32em">{compact(s.values[n - 1])}</text>
            </g>
          ))}

          {hover && mode === 'line' && (
            <g>
              <line className="crosshair" x1={x(hover.i)} x2={x(hover.i)} y1={m.top} y2={m.top + innerH} />
              {series.map((s) => (
                <circle key={s.key} className="dot" cx={x(hover.i)} cy={y(s.values[hover.i])} r={4} fill={seriesColor(s.colorIndex)} />
              ))}
            </g>
          )}

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
            <div key={s.key} className="tooltip-row">
              <span className="swatch" style={{ background: seriesColor(s.colorIndex) }} />
              <span className="tooltip-label">{s.label}</span>
              <span className="num">{num(s.values[hover.i])}</span>
            </div>
          ))}
        </Tooltip>
      )}
    </div>
  )
}

const truncate = (s: string, max: number) => (s.length > max ? s.slice(0, max - 1) + '…' : s)

export interface FunnelGroup { label: string; colorIndex: number; counts: number[] }

export function FunnelChart({ steps, groups }: { steps: string[]; groups: FunnelGroup[] }) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<{ step: number; group: number; x: number; y: number } | null>(null)

  const labelW = Math.min(180, Math.max(110, width * 0.22))
  const valueW = 150
  const barH = 20
  const rowGap = 18
  const rowH = groups.length * barH + (groups.length - 1) * 2
  const height = steps.length * (rowH + rowGap)
  const trackW = Math.max(0, width - labelW - valueW)
  const maxFirst = Math.max(1, ...groups.map((g) => g.counts[0] ?? 0))

  return (
    <div className="chart" ref={ref}>
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label="Funnel chart">
          {steps.map((name, s) => {
            const top = s * (rowH + rowGap) + rowGap / 2
            return (
              <g key={s}>
                <text className="step-label" x={0} y={top + rowH / 2} dy="0.32em">
                  <tspan className="step-index">{s + 1}.</tspan> {truncate(name, 20)}
                </text>
                {groups.map((g, k) => {
                  const by = top + k * (barH + 2)
                  const c = g.counts[s] ?? 0
                  const w = (c / maxFirst) * trackW
                  return (
                    <g key={g.label}
                      onPointerMove={(e) => {
                        const box = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect()
                        setHover({ step: s, group: k, x: e.clientX - box.left, y: e.clientY - box.top })
                      }}
                      onPointerLeave={() => setHover(null)}>
                      <rect className="track" x={labelW} y={by} width={trackW} height={barH} rx={4} />
                      {w > 0 && <path fill={seriesColor(g.colorIndex)} d={barPath(labelW, by, Math.max(w, 2), barH)} />}
                      {groups.length === 1 && (
                        <text className="bar-value" x={labelW + trackW + 10} y={by + barH / 2} dy="0.32em">
                          {num(c)} · {pct(g.counts[0] ? c / g.counts[0] : 0)}
                        </text>
                      )}
                    </g>
                  )
                })}
              </g>
            )
          })}
        </svg>
      )}
      {hover && (() => {
        const g = groups[hover.group]
        const c = g.counts[hover.step] ?? 0
        const first = g.counts[0] ?? 0
        const prev = hover.step > 0 ? g.counts[hover.step - 1] ?? 0 : c
        return (
          <Tooltip x={hover.x} y={hover.y} width={width}>
            <div className="tooltip-title">{hover.step + 1}. {steps[hover.step]}</div>
            {groups.length > 1 && (
              <div className="tooltip-row"><span className="swatch" style={{ background: seriesColor(g.colorIndex) }} /><span className="tooltip-label">{g.label}</span></div>
            )}
            <div className="tooltip-row"><span className="tooltip-label">Persons</span><span className="num">{num(c)}</span></div>
            <div className="tooltip-row"><span className="tooltip-label">Of first step</span><span className="num">{pct(first ? c / first : 0)}</span></div>
            {hover.step > 0 && (
              <div className="tooltip-row"><span className="tooltip-label">Drop-off from previous</span><span className="num">{pct(prev ? 1 - c / prev : 0)}</span></div>
            )}
          </Tooltip>
        )
      })()}
    </div>
  )
}
