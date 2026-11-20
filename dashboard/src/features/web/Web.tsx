import type { Filter, WebAnalyticsResponse, WebBreakdownRow, WebDimension, WebSummary } from '../../api/types'
import { api } from '../../api/client'
import { readState, toDateRange, type Range } from '../../app/route'
import { Legend } from '../../components/charts/common'
import { TimeChart } from '../../components/charts/TimeChart'
import { completeFilters, FilterEditor } from '../../components/editors'
import { Card, Segmented, StatTile, Status } from '../../components/ui'
import { webCsv } from '../../lib/csv'
import { duration, ms, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { AnnotationNotes, useAnnotations } from '../insights/Annotations'
import { SaveInsight } from '../insights/SaveInsight'

type PagesView = 'top' | 'entry'
type SourceView = 'referring_domain' | 'utm_source' | 'utm_medium' | 'utm_campaign'
type DeviceView = 'device_type' | 'browser' | 'os'
export interface WebState { filters: Filter[]; pages: PagesView; sources: SourceView; devices: DeviceView }

export const WEB_DEFAULT: WebState = { filters: [], pages: 'top', sources: 'referring_domain', devices: 'device_type' }

const SOURCE_VIEWS: { id: SourceView; label: string }[] = [
  { id: 'referring_domain', label: 'Referrer' },
  { id: 'utm_source', label: 'UTM source' },
  { id: 'utm_medium', label: 'Medium' },
  { id: 'utm_campaign', label: 'Campaign' },
]
const DEVICE_VIEWS: { id: DeviceView; label: string }[] = [
  { id: 'device_type', label: 'Device' },
  { id: 'browser', label: 'Browser' },
  { id: 'os', label: 'OS' },
]

export function webBody(state: WebState, range: Range) {
  return { filters: completeFilters(state.filters), ...toDateRange(range) }
}

export function change(current: number, previous: number): string {
  if (previous === 0) return current === 0 ? 'no change' : 'new'
  const r = (current - previous) / previous
  return `${r >= 0 ? '+' : '−'}${pct(Math.abs(r))}`
}

export function pointChange(current: number | null, previous: number | null): string {
  if (current === null || previous === null) return '—'
  const d = (current - previous) * 100
  return `${d >= 0 ? '+' : '−'}${Math.abs(d).toLocaleString('en-US', { maximumFractionDigits: 1 })} pp`
}

export function valueLabel(dimension: WebDimension | 'pathname', value: string | null): string {
  if (dimension === 'referring_domain' && (value === null || value === '$direct')) return 'Direct'
  return value === null || value === '' ? '(none)' : value
}

export const webEmpty = (d?: WebAnalyticsResponse) => !!d && d.current.pageviews === 0 && d.previous.pageviews === 0

export function Web({ params, range, onChange, onInsight }: {
  params: URLSearchParams; range: Range; onChange: (q: string) => void; onInsight: (id?: string) => void
}) {
  const state = readState(params, WEB_DEFAULT)
  const set = (patch: Partial<WebState>) => onChange(JSON.stringify({ ...state, ...patch }))
  const request = useDebounced(JSON.stringify(webBody(state, range)))
  const q = useQuery((signal) => api.web(JSON.parse(request), signal), [request])
  const d = q.data

  return (
    <section className="content">
      <div className="card web-filters">
        <FilterEditor filters={state.filters} onChange={(filters) => set({ filters })} />
      </div>
      <SaveInsight kind="web" query={state} params={params} onInsight={onInsight} csv={d && webCsv(d)} />
      <Status state={q} empty={webEmpty(d)} emptyTitle="No pageviews in this range"
        emptyText="Web analytics counts $pageview events. The JS SDK sends them automatically.">
        {d && <WebReport d={d} state={state} set={set} />}
      </Status>
      <p className="footnote">A session is the SDK's session id; it ends after 30 minutes without activity. Sessions without a pageview are left out.
        A bounce is a session with one pageview. Session duration runs from the session's first to its last event. Entry page, source and device come from the session's first pageview.</p>
    </section>
  )
}

function WebReport({ d, state, set }: { d: WebAnalyticsResponse; state: WebState; set: (patch: Partial<WebState>) => void }) {
  const c = d.current
  const p = d.previous
  const vs = 'vs previous'
  const rows = (dim: WebDimension) => d.breakdowns.find((b) => b.dimension === dim)?.rows ?? []
  const annotations = useAnnotations(d.series.map((s) => s.bucket), d.interval)
  return (
    <>
      <div className="stats web-stats">
        <StatTile label="Visitors" value={num(c.visitors)} hint={`${change(c.visitors, p.visitors)} ${vs}`} swatch="var(--series-1)" testId="web-visitors" />
        <StatTile label="Pageviews" value={num(c.pageviews)} hint={`${change(c.pageviews, p.pageviews)} ${vs}`} swatch="var(--series-2)" testId="web-pageviews" />
        <StatTile label="Sessions" value={num(c.sessions)} hint={`${change(c.sessions, p.sessions)} ${vs}`} testId="web-sessions" />
        <StatTile label="Bounce rate" value={c.bounce_rate === null ? '—' : pct(c.bounce_rate)} hint={`${pointChange(c.bounce_rate, p.bounce_rate)} ${vs}`} testId="web-bounce-rate" />
        <StatTile label="Session duration" value={c.avg_session_seconds === null ? '—' : duration(c.avg_session_seconds)}
          hint={`${p.avg_session_seconds === null || c.avg_session_seconds === null ? '—' : change(c.avg_session_seconds, p.avg_session_seconds)} ${vs}`} testId="web-duration" />
      </div>
      <Card testId="web-chart" title="Visitors and pageviews" meta={`${d.timezone} · ${ms(d.took_ms)}`}>
        <Legend items={[{ label: 'Visitors', colorIndex: 0 }, { label: 'Pageviews', colorIndex: 1 }]} />
        <TimeChart buckets={d.series.map((s) => s.bucket)} interval={d.interval} mode="line" notes={annotations.notes} series={[
          { key: 'visitors', label: 'Visitors', colorIndex: 0, values: d.series.map((s) => s.visitors) },
          { key: 'pageviews', label: 'Pageviews', colorIndex: 1, values: d.series.map((s) => s.pageviews) },
        ]} />
        <AnnotationNotes notes={annotations.notes} from={annotations.from} to={annotations.to} onChange={annotations.reload} />
        <p className="footnote">Previous period: the same length right before {d.date_from.slice(0, 10)}; the tiles compare against it. <PreviousSummary p={p} /></p>
      </Card>
      <div className="web-grid">
        <Card testId="web-pages" title="Pages" actions={
          <Segmented<PagesView> size="sm" label="Pages view" value={state.pages} onChange={(pages) => set({ pages })}
            options={[{ id: 'top', label: 'Top pages' }, { id: 'entry', label: 'Entry pages' }]} />
        }>
          {state.pages === 'top' ? <TopPages pages={d.pages} /> : <SessionTable dimension="entry_page" label="Entry page" rows={rows('entry_page')} />}
        </Card>
        <Card testId="web-sources" title="Traffic sources" actions={
          <Segmented<SourceView> size="sm" label="Source view" value={state.sources} onChange={(sources) => set({ sources })} options={SOURCE_VIEWS} />
        }>
          <SessionTable dimension={state.sources} label={SOURCE_VIEWS.find((v) => v.id === state.sources)!.label} rows={rows(state.sources)} />
        </Card>
        <Card testId="web-devices" title="Devices" actions={
          <Segmented<DeviceView> size="sm" label="Device view" value={state.devices} onChange={(devices) => set({ devices })} options={DEVICE_VIEWS} />
        }>
          <SessionTable dimension={state.devices} label={DEVICE_VIEWS.find((v) => v.id === state.devices)!.label} rows={rows(state.devices)} />
        </Card>
      </div>
    </>
  )
}

function PreviousSummary({ p }: { p: WebSummary }) {
  return <>It had {num(p.visitors)} visitors, {num(p.pageviews)} pageviews and {num(p.sessions)} sessions.</>
}

function Share({ value, max }: { value: number; max: number }) {
  return <div className="path-bar-track"><div className="path-bar" style={{ width: `${Math.max(2, (value / Math.max(1, max)) * 100)}%` }} /></div>
}

function TopPages({ pages }: { pages: WebAnalyticsResponse['pages'] }) {
  if (pages.length === 0) return <p className="editor-hint">No pageviews.</p>
  const max = Math.max(...pages.map((r) => r.pageviews))
  return (
    <div className="table-wrap">
      <table className="web-table">
        <thead><tr><th>Page</th><th className="num">Visitors</th><th className="num">Pageviews</th></tr></thead>
        <tbody>
          {pages.map((r) => (
            <tr key={r.pathname ?? ''} className="web-row" data-dimension="pathname" data-value={r.pathname ?? ''}>
              <td className="web-label"><span className="truncate" title={r.pathname ?? undefined}>{valueLabel('pathname', r.pathname)}</span><Share value={r.pageviews} max={max} /></td>
              <td className="num">{num(r.visitors)}</td>
              <td className="num strong">{num(r.pageviews)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function SessionTable({ dimension, label, rows }: { dimension: WebDimension; label: string; rows: WebBreakdownRow[] }) {
  if (rows.length === 0) return <p className="editor-hint">No sessions.</p>
  const max = Math.max(...rows.map((r) => r.sessions))
  return (
    <div className="table-wrap">
      <table className="web-table">
        <thead><tr><th>{label}</th><th className="num">Sessions</th><th className="num">Visitors</th><th className="num">Bounce</th><th className="num">Duration</th></tr></thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.value ?? ''} className="web-row" data-dimension={dimension} data-value={r.value ?? ''}>
              <td className="web-label"><span className="truncate" title={r.value ?? undefined}>{valueLabel(dimension, r.value)}</span><Share value={r.sessions} max={max} /></td>
              <td className="num strong">{num(r.sessions)}</td>
              <td className="num">{num(r.visitors)}</td>
              <td className="num">{pct(r.bounce_rate)}</td>
              <td className="num nowrap">{duration(r.avg_session_seconds)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
