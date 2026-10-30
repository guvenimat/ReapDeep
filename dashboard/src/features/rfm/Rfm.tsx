import { useState } from 'react'
import { api, errorMessage } from '../../api/client'
import type { Filter, RfmResponse, RfmSegmentKey } from '../../api/types'
import { readState } from '../../app/route'
import { completeFilters, EditorSection, FilterEditor } from '../../components/editors'
import { Icon } from '../../components/icons'
import { Card, FormMessage, Segmented, StatTile, Status } from '../../components/ui'
import { compact, ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { SaveInsight } from '../insights/SaveInsight'
import { RFM_SEGMENT_NAMES, RFM_WINDOWS } from './segments'

export interface RfmState { window_days: number; purchase_event: string; amount_property: string; filters: Filter[] }

export const RFM_DEFAULT: RfmState = { window_days: 365, purchase_event: 'purchase', amount_property: 'amount', filters: [] }

export function rfmBody(state: RfmState) {
  return {
    window_days: state.window_days,
    purchase_event: state.purchase_event.trim() || undefined,
    amount_property: state.amount_property.trim() || undefined,
    filters: completeFilters(state.filters),
  }
}

const money = (n: number, currencies: string[]) => {
  const text = n >= 10_000 ? compact(n) : n.toLocaleString('en-US', { maximumFractionDigits: 2 })
  return currencies.length === 1 ? `${text} ${currencies[0]}` : text
}

const heatClass = (ratio: number) => (ratio <= 0 ? 'heat-0' : `heat-${Math.min(6, Math.ceil(ratio * 6))}`)

export function RfmGrid({ data, active, onActive }: { data: RfmResponse; active?: RfmSegmentKey; onActive?: (s?: RfmSegmentKey) => void }) {
  const max = Math.max(1, ...data.grid.map((c) => c.persons))
  const cell = (r: number, f: number) => data.grid.find((c) => c.r === r && c.f === f)!
  return (
    <div className="rfm-grid-wrap">
      <div className="rfm-axis-y">Recency score · 5 = bought most recently</div>
      <table className="rfm-grid" data-testid="rfm-grid">
        <tbody>
          {[5, 4, 3, 2, 1].map((r) => (
            <tr key={r}>
              <th scope="row" className="num">R{r}</th>
              {[1, 2, 3, 4, 5].map((f) => {
                const c = cell(r, f)
                const dim = active !== undefined && active !== c.segment
                return (
                  <td key={f} className={`heat ${heatClass(c.persons / max)}${dim ? ' dim' : ''}${active === c.segment ? ' on' : ''}`}
                    title={`R${r} · F${f} · ${RFM_SEGMENT_NAMES[c.segment]}: ${num(c.persons)} customers (${pct(data.customers ? c.persons / data.customers : 0)})`}
                    onPointerEnter={() => onActive?.(c.segment)} onPointerLeave={() => onActive?.(undefined)}>
                    <span className="rfm-count num">{compact(c.persons)}</span>
                    <span className="rfm-seg">{RFM_SEGMENT_NAMES[c.segment]}</span>
                  </td>
                )
              })}
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th />
            {[1, 2, 3, 4, 5].map((f) => <th key={f} className="num">F{f}</th>)}
          </tr>
        </tfoot>
      </table>
      <div className="rfm-axis-x">Frequency score · 5 = most orders</div>
    </div>
  )
}

export function Rfm({ params, onChange, onInsight, onCohortCreated }: {
  params: URLSearchParams; onChange: (q: string) => void; onInsight: (id?: string) => void; onCohortCreated: () => void
}) {
  const state = readState(params, RFM_DEFAULT)
  const set = (patch: Partial<RfmState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(rfmBody(state)))
  const q = useQuery((signal) => api.rfm(JSON.parse(request), signal), [request])
  const [active, setActive] = useState<RfmSegmentKey>()
  const [created, setCreated] = useState<{ text: string; id?: number; error?: boolean }>()
  const [busy, setBusy] = useState<RfmSegmentKey>()

  const filtered = completeFilters(state.filters).length > 0
  const d = q.data
  const champions = d?.segments.find((s) => s.key === 'champions')
  const atRisk = d?.segments.filter((s) => s.key === 'at_risk' || s.key === 'cant_lose')

  const createCohort = async (key: RfmSegmentKey) => {
    setBusy(key)
    setCreated(undefined)
    try {
      const event = state.purchase_event.trim()
      const cohort = await api.createCohort({
        name: `RFM · ${RFM_SEGMENT_NAMES[key]}`,
        description: `Customers in the ${RFM_SEGMENT_NAMES[key]} RFM segment over the last ${state.window_days} days`,
        kind: 'dynamic',
        definition: { groups: [{ conditions: [{ type: 'rfm_segment', segments: [key], within_days: state.window_days, event: event && event !== 'purchase' ? event : undefined }] }] },
      })
      onCohortCreated()
      setCreated({ text: `Created cohort "${cohort.name}"`, id: cohort.id })
    } catch (err) {
      setCreated({ text: errorMessage(err), error: true })
    } finally {
      setBusy(undefined)
    }
  }

  return (
    <div className="layout">
      <aside className="panel">
        <EditorSection title="Purchases" hint="Each purchase event is one item; orders are counted by order_id, or one per event when it is missing.">
          <label className="field"><span>Purchase event</span>
            <input list="dl-events" value={state.purchase_event} onChange={(e) => set({ purchase_event: e.target.value })} aria-label="Purchase event" />
          </label>
          <label className="field"><span>Amount property</span>
            <input list="dl-event-props" value={state.amount_property} onChange={(e) => set({ amount_property: e.target.value })} aria-label="Amount property" />
          </label>
        </EditorSection>
        <EditorSection title="Window" hint="Scores compare customers who bought in this window with each other.">
          <Segmented<number> size="sm" label="Window" value={state.window_days}
            options={RFM_WINDOWS.map((w) => ({ id: w, label: `${w} days` }))} onChange={(window_days) => set({ window_days })} />
        </EditorSection>
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </aside>
      <section className="content">
        <SaveInsight kind="rfm" query={state} params={params} onInsight={onInsight} />
        <Status state={q} empty={d?.customers === 0} emptyTitle="No purchases in this window" emptyText="Send purchase events with an amount, or choose a longer window.">
          {d && (
            <>
              {d.currencies.length > 1 && (
                <div className="notice warn" role="status">Purchases use {d.currencies.length > 10 ? 'more than 10' : d.currencies.length} currencies ({d.currencies.slice(0, 10).join(', ')}); monetary values are summed without conversion.</div>
              )}
              <div className="stats">
                <StatTile label="Customers" value={num(d.customers)} hint={`last ${d.window_days} days`} testId="rfm-customers" />
                <StatTile label="Revenue" value={money(d.revenue, d.currencies)} hint={`${money(d.customers ? d.revenue / d.customers : 0, d.currencies)} per customer`} />
                {champions && <StatTile label="Champions" value={pct(champions.share)} hint={`${pct(champions.revenue_share)} of revenue`} />}
                {atRisk && <StatTile label="At risk + can't lose" value={num(atRisk.reduce((s, x) => s + x.persons, 0))}
                  hint={`${money(atRisk.reduce((s, x) => s + x.revenue, 0), d.currencies)} revenue`} />}
              </div>
              <div className="rfm-cards">
                <Card testId="rfm-grid-card" title="Recency × frequency" meta={`${num(d.customers)} customers · ${ms(d.took_ms)}`}>
                  <RfmGrid data={d} active={active} onActive={setActive} />
                  <p className="footnote">
                    Scores are percentiles among these customers; equal values share a score.
                    {' '}R5 = last purchase within {range(d.thresholds.recency[4])} days · F5 = {range(d.thresholds.frequency[4])} orders.
                  </p>
                </Card>
                <Card testId="rfm-segments-card" title="Segments" meta={filtered ? 'Cohorts use all customers; clear filters to create one' : 'Turn a segment into a cohort with the people icon'}>
                  <FormMessage error={created?.error ? created.text : undefined} />
                  {created && !created.error && (
                    <div className="form-message good" role="status">{created.text} · <a href={`#/cohorts/${created.id}`}>open</a></div>
                  )}
                  <div className="table-wrap">
                    <table className="rfm-segments" data-testid="rfm-segments">
                      <thead>
                        <tr>
                          <th>Segment</th>
                          <th className="num" title="Customers and share of all customers">Customers</th>
                          <th className="num" title="Average days since the last purchase">Recency</th>
                          <th className="num" title="Average number of orders">Orders</th>
                          <th className="num" title="Average revenue per customer">Value</th>
                          <th className="num" title="Share of revenue">Revenue</th>
                        </tr>
                      </thead>
                      <tbody>
                        {d.segments.map((s) => (
                          <tr key={s.key} className={active === s.key ? 'active' : undefined}
                            onPointerEnter={() => setActive(s.key)} onPointerLeave={() => setActive(undefined)}>
                            <td>
                              <div className="segment-cell">
                                <div><span className="strong">{s.name}</span><span className="cell-sub">{s.description}</span></div>
                                <button type="button" className="icon-button" disabled={filtered || s.persons === 0 || busy !== undefined}
                                  aria-label={`Create a cohort of ${s.name}`} title={`Create a cohort of ${s.name}`}
                                  onClick={() => createCohort(s.key)} data-testid={`cohort-${s.key}`}>
                                  <Icon name={busy === s.key ? 'refresh' : 'users'} />
                                </button>
                              </div>
                            </td>
                            <td className="num"><span className="strong">{num(s.persons)}</span><span className="cell-sub">{pct(s.share)}</span></td>
                            <td className="num muted">{s.avg_recency_days === null ? '—' : `${num(Math.round(s.avg_recency_days))} d`}</td>
                            <td className="num muted">{s.avg_frequency === null ? '—' : s.avg_frequency.toFixed(1)}</td>
                            <td className="num muted nowrap">{s.avg_monetary === null ? '—' : money(Math.round(s.avg_monetary), d.currencies)}</td>
                            <td className="num">{pct(s.revenue_share)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </Card>
              </div>
            </>
          )}
        </Status>
      </section>
    </div>
  )
}

const range = (r: { min: number | null; max: number | null }) =>
  r.min === null ? '—' : r.min === r.max ? num(r.min) : `${num(r.min!)}–${num(r.max!)}`

