import { useState } from 'react'
import { num, pct } from '../../lib/format'
import { useWidth } from '../../lib/hooks'
import { barPath, seriesColor, Tooltip, TooltipRow } from './common'

export interface FunnelGroup { label: string; colorIndex: number; counts: number[] }

const truncate = (s: string, max: number) => (s.length > max ? s.slice(0, max - 1) + '…' : s)

export function FunnelChart({ steps, groups }: { steps: string[]; groups: FunnelGroup[] }) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<{ step: number; group: number; x: number; y: number } | null>(null)

  const single = groups.length === 1
  const labelH = 26
  const barH = single ? 26 : 16
  const barGap = 3
  const rowGap = 14
  const barsH = groups.length * barH + (groups.length - 1) * barGap
  const rowH = labelH + barsH + rowGap
  const height = steps.length * rowH
  const maxFirst = Math.max(1, ...groups.map((g) => g.counts[0] ?? 0))
  const maxChars = Math.max(12, Math.floor(width / 9) - 18)

  return (
    <div className="chart" ref={ref}>
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label="Funnel chart">
          {steps.map((name, s) => {
            const top = s * rowH
            const g0 = groups[0]
            const c0 = g0?.counts[s] ?? 0
            const prev = s > 0 ? g0?.counts[s - 1] ?? 0 : 0
            return (
              <g key={s}>
                <text className="step-label" x={0} y={top + 15}>
                  <tspan className="step-index">{String(s + 1).padStart(2, '0')}</tspan>
                  <tspan dx={10}>{truncate(name, maxChars)}</tspan>
                </text>
                {single && (
                  <text className="bar-value" x={width} y={top + 15} textAnchor="end">
                    <tspan className="strong-value">{num(c0)}</tspan>
                    <tspan dx={8}>{pct(g0.counts[0] ? c0 / g0.counts[0] : 0)}</tspan>
                    {s > 0 && <tspan dx={8} className="drop">−{pct(prev ? 1 - c0 / prev : 0)}</tspan>}
                  </text>
                )}
                {groups.map((g, k) => {
                  const by = top + labelH + k * (barH + barGap)
                  const c = g.counts[s] ?? 0
                  const w = (c / maxFirst) * width
                  return (
                    <g key={g.label}
                      onPointerMove={(e) => {
                        const box = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect()
                        setHover({ step: s, group: k, x: e.clientX - box.left, y: e.clientY - box.top })
                      }}
                      onPointerLeave={() => setHover(null)}>
                      <rect className="track" x={0} y={by} width={width} height={barH} rx={4} />
                      {w > 0 && (
                        <path fill={seriesColor(g.colorIndex)} d={barPath(0, by, Math.max(w, 3), barH)}
                          opacity={hover && (hover.step !== s || hover.group !== k) ? 0.7 : 1} />
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
            {groups.length > 1 && <TooltipRow color={seriesColor(g.colorIndex)} label={g.label} />}
            <TooltipRow label="Persons" value={num(c)} />
            <TooltipRow label="Of first step" value={pct(first ? c / first : 0)} />
            {hover.step > 0 && <TooltipRow label="Drop-off from previous" value={pct(prev ? 1 - c / prev : 0)} />}
          </Tooltip>
        )
      })()}
    </div>
  )
}
