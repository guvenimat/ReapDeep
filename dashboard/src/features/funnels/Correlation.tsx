import { api } from '../../api/client'
import type { FunnelCorrelation } from '../../api/types'
import { Icon } from '../../components/icons'
import { Card, Status } from '../../components/ui'
import { ms, num, pct } from '../../lib/format'
import { useQuery } from '../../lib/hooks'

const odds = (v: number) => (v >= 1 ? `${v >= 100 ? '>100' : v.toFixed(1)}×` : 1 / v >= 100 ? '<1/100' : `1/${(1 / v).toFixed(1)}`)

function Rows({ title, rows, positive }: { title: string; rows: FunnelCorrelation[]; positive: boolean }) {
  if (rows.length === 0) return null
  return (
    <div className="ai-section">
      <div className="eyebrow">{title}</div>
      <div className="table-wrap">
        <table className={positive ? 'corr positive' : 'corr negative'}>
          <thead><tr><th>Event</th><th className="num">Converted who did it</th><th className="num">Dropped who did it</th><th className="num">Odds</th></tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.event} data-event={r.event}>
                <td><span className="chip">{r.event}</span></td>
                <td className="num">{pct(r.converted_share)} <span className="muted small">{num(r.converted_with)}</span></td>
                <td className="num">{pct(r.dropped_share)} <span className="muted small">{num(r.dropped_with)}</span></td>
                <td className={`num strong ${positive ? 'good' : 'form-message error'}`}>{odds(r.odds_ratio)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

export function CorrelationCard({ request }: { request: string }) {
  const q = useQuery((signal) => api.funnelCorrelation(JSON.parse(request), signal), [request])
  const d = q.data
  return (
    <Card className="corr-card" title={<><Icon name="sparkles" />What sets converters apart</>}
      meta={d ? `${num(d.converted)} converted · ${num(d.dropped)} dropped · events done by at least ${d.min_persons} persons in the range · ${ms(d.took_ms)}` : undefined}>
      <Status state={q} empty={!!d && d.positive.length === 0 && d.negative.length === 0} emptyTitle="No clear differences"
        emptyText="No other event is clearly more or less common among the people who converted.">
        {d && (
          <>
            <Rows title="More common among people who converted" rows={d.positive} positive />
            <Rows title="More common among people who dropped off" rows={d.negative} positive={false} />
            <p className="footnote">Odds compare how likely converters were to do the event with how likely the others were. It shows association, not cause: test a change with an experiment before relying on it.</p>
          </>
        )}
      </Status>
    </Card>
  )
}
