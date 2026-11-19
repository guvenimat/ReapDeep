import { useState } from 'react'
import { api } from '../../api/client'
import type { FunnelCorrelation } from '../../api/types'
import { SuggestInput } from '../../components/editors'
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
    <>
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
      <PropertyCorrelationCard request={request} />
    </>
  )
}

function PropertyCorrelationCard({ request }: { request: string }) {
  const funnel = JSON.parse(request) as { steps?: ({ event?: string } | { action?: string })[] }
  const first = funnel.steps?.find((s): s is { event: string } => 'event' in s && !!s.event)?.event ?? ''
  const [event, setEvent] = useState(first)
  const [property, setProperty] = useState('')
  return (
    <Card className="corr-card" title={<><Icon name="sparkles" />What property values set converters apart</>}
      meta="Compares one event property between the people who converted and the people who dropped off">
      <div className="row" data-testid="property-correlation-picker">
        <label className="field" htmlFor="correlation-event"><span>Event</span>
          <SuggestInput id="correlation-event" list="events" value={event} placeholder="event" onChange={setEvent} aria-label="Property correlation event" />
        </label>
        <label className="field" htmlFor="correlation-property"><span>Property</span>
          <SuggestInput id="correlation-property" list="event-props" value={property} placeholder="property" onChange={setProperty} aria-label="Property correlation property" />
        </label>
      </div>
      {event && property
        ? <PropertyRows body={{ ...funnel, event, property }} />
        : <p className="editor-hint">Pick an event and a property to compare its values. The value comes from each person's first matching event in the range.</p>}
    </Card>
  )
}

function PropertyRows({ body }: { body: object }) {
  const key = JSON.stringify(body)
  const q = useQuery((signal) => api.funnelPropertyCorrelation(body, signal), [key])
  const d = q.data
  return (
    <Status state={q} empty={!!d && d.values.length === 0} emptyTitle="No values to compare"
      emptyText="No value was seen by enough persons in both groups. Pick another event or property.">
      {d && (
        <>
          <div className="table-wrap">
            <table className="corr positive" data-testid="property-correlation">
              <thead><tr><th>{d.property}</th><th className="num">Converted who had it</th><th className="num">Dropped who had it</th><th className="num">Odds</th><th className="num">p-value</th></tr></thead>
              <tbody>
                {d.values.map((v) => (
                  <tr key={v.value} data-value={v.value}>
                    <td><span className="chip">{v.value}</span></td>
                    <td className="num">{pct(v.converted_share)} <span className="muted small">{num(v.converted_with)}</span></td>
                    <td className="num">{pct(v.dropped_share)} <span className="muted small">{num(v.dropped_with)}</span></td>
                    <td className={`num strong ${v.odds_ratio >= 1 ? 'good' : 'form-message error'}`}>{odds(v.odds_ratio)}</td>
                    <td className="num">{v.p_value === null ? '—' : v.p_value < 0.0001 ? '< 0.0001' : v.p_value.toFixed(3)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="footnote">{num(d.converted)} converted and {num(d.dropped)} dropped off; values seen by at least {d.min_persons} persons.
            A small p-value is evidence of an association, not of cause.</p>
        </>
      )}
    </Status>
  )
}
