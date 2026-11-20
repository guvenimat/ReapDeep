import { api } from '../../api/client'
import type { Insight } from '../../api/types'
import type { Range } from '../../app/route'
import { Legend } from '../../components/charts/common'
import { FunnelChart } from '../../components/charts/FunnelChart'
import { TimeChart } from '../../components/charts/TimeChart'
import { StatTile, Status } from '../../components/ui'
import { num, pct } from '../../lib/format'
import { useQuery } from '../../lib/hooks'
import { LIFECYCLE_DEFAULT, lifecycleBody, lifecycleEmpty, LifecycleLegend, type LifecycleState } from '../lifecycle/Lifecycle'
import { LifecycleChart } from '../../components/charts/LifecycleChart'
import { RFM_DEFAULT, rfmBody, RfmGrid, type RfmState } from '../rfm/Rfm'
import { FUNNEL_DEFAULT, funnelBody, funnelGroups, type FunnelState } from '../funnels/Funnels'
import { PathList, PATHS_DEFAULT, pathsBody, type PathsState } from '../paths/Paths'
import { RETENTION_DEFAULT, retentionBody, RetentionTable, type RetentionState } from '../retention/Retention'
import { STICKINESS_DEFAULT, stickinessBody, stickinessRegulars, type StickinessState } from '../stickiness/Stickiness'
import { TREND_DEFAULT, trendBody, trendChartSeries, trendInterval, visibleResults, type TrendState } from '../trends/Trends'
import { WEB_DEFAULT, webBody, webEmpty, type WebState } from '../web/Web'
import { useAnnotations } from './Annotations'

export function InsightTile({ insight, range }: { insight: Insight; range: Range }) {
  if (insight.kind === 'trends') return <TrendTile state={{ ...TREND_DEFAULT, ...(insight.query as Partial<TrendState>) }} range={range} />
  if (insight.kind === 'retention') return <RetentionTile state={{ ...RETENTION_DEFAULT, ...(insight.query as Partial<RetentionState>) }} range={range} />
  if (insight.kind === 'lifecycle') return <LifecycleTile state={{ ...LIFECYCLE_DEFAULT, ...(insight.query as Partial<LifecycleState>) }} range={range} />
  if (insight.kind === 'rfm') return <RfmTile state={{ ...RFM_DEFAULT, ...(insight.query as Partial<RfmState>) }} />
  if (insight.kind === 'paths') return <PathsTile state={{ ...PATHS_DEFAULT, ...(insight.query as Partial<PathsState>) }} range={range} />
  if (insight.kind === 'stickiness') return <StickinessTile state={{ ...STICKINESS_DEFAULT, ...(insight.query as Partial<StickinessState>) }} range={range} />
  if (insight.kind === 'web') return <WebTile state={{ ...WEB_DEFAULT, ...(insight.query as Partial<WebState>) }} range={range} />
  return <FunnelTile state={{ ...FUNNEL_DEFAULT, ...(insight.query as Partial<FunnelState>) }} range={range} />
}

function TrendTile({ state, range }: { state: TrendState; range: Range }) {
  const body = JSON.stringify(trendBody(state, range))
  const q = useQuery((signal) => api.trends(JSON.parse(body), signal), [body])
  const series = trendChartSeries(q.data)
  const current = visibleResults(q.data).filter((r) => !r.compare)
  const buckets = current[0]?.data.map((d) => d.bucket) ?? []
  const interval = trendInterval(state, range)
  const annotations = useAnnotations(buckets, interval)
  const total = current.length === 1 ? current[0].total : undefined
  return (
    <Status state={q} empty={(q.data?.results ?? []).every((r) => r.total === 0)}>
      {total !== undefined && <div className="tile-total" data-testid="tile-total">{num(total)}</div>}
      <Legend items={series.map((s) => ({ label: s.label, colorIndex: s.colorIndex }))} />
      <TimeChart buckets={buckets} interval={interval} series={series} mode={state.chart} height={220} notes={annotations.notes} />
    </Status>
  )
}

function FunnelTile({ state, range }: { state: FunnelState; range: Range }) {
  const body = JSON.stringify(funnelBody(state, range))
  const q = useQuery((signal) => api.funnels(JSON.parse(body), signal), [body])
  const groups = funnelGroups(q.data)
  const steps = q.data?.results.length === 1 ? q.data.results[0].steps : undefined
  const last = steps?.[steps.length - 1]
  return (
    <Status state={q} empty={groups.every((g) => g.counts[0] === 0)}>
      {last && <div className="tile-total">{pct(last.conversion_from_first)}<span className="tile-total-hint">overall conversion</span></div>}
      <Legend items={groups.map((g) => ({ label: g.label, colorIndex: g.colorIndex }))} />
      <FunnelChart steps={q.data?.results[0]?.steps.map((s) => s.event) ?? []} groups={groups} />
    </Status>
  )
}

function RetentionTile({ state, range }: { state: RetentionState; range: Range }) {
  const body = JSON.stringify(retentionBody(state, range))
  const q = useQuery((signal) => api.retention(JSON.parse(body), signal), [body])
  return (
    <Status state={q} empty={q.data?.cohorts.every((c) => c.size === 0)}>
      {q.data && <RetentionTable data={q.data} maxCohorts={5} />}
    </Status>
  )
}

function LifecycleTile({ state, range }: { state: LifecycleState; range: Range }) {
  const body = JSON.stringify(lifecycleBody(state, range))
  const q = useQuery((signal) => api.lifecycle(JSON.parse(body), signal), [body])
  return (
    <Status state={q} empty={lifecycleEmpty(q.data)}>
      <LifecycleLegend />
      {q.data && <LifecycleChart data={q.data} height={220} />}
    </Status>
  )
}

function RfmTile({ state }: { state: RfmState }) {
  const body = JSON.stringify(rfmBody(state))
  const q = useQuery((signal) => api.rfm(JSON.parse(body), signal), [body])
  return (
    <Status state={q} empty={q.data?.customers === 0} emptyTitle="No purchases in this window">
      {q.data && <div className="tile-total">{num(q.data.customers)}<span className="tile-total-hint">customers in the last {q.data.window_days} days</span></div>}
      {q.data && <RfmGrid data={q.data} />}
    </Status>
  )
}

function PathsTile({ state, range }: { state: PathsState; range: Range }) {
  const body = JSON.stringify(pathsBody(state, range))
  const q = useQuery((signal) => api.paths(JSON.parse(body), signal), [body])
  return (
    <Status state={q} empty={q.data?.persons === 0}>
      {q.data && <PathList data={q.data} limit={6} />}
    </Status>
  )
}

function StickinessTile({ state, range }: { state: StickinessState; range: Range }) {
  const body = JSON.stringify(stickinessBody(state, range))
  const q = useQuery((signal) => api.stickiness(JSON.parse(body), signal), [body])
  const d = q.data
  const unit = d?.interval === 'week' ? 'weeks' : 'days'
  return (
    <Status state={q} empty={d?.persons === 0}>
      {d && (
        <div className="stats">
          <StatTile label="Persons" value={num(d.persons)} hint={`did ${d.event} at least once`} />
          <StatTile label={`Average ${unit} active`} value={d.average_periods === null ? '-' : String(d.average_periods)} hint={`out of ${d.total_periods}`} />
          <StatTile label="Regulars" value={d.persons ? pct(stickinessRegulars(d) / d.persons) : '-'} hint={`active in at least half of the ${unit}`} />
        </div>
      )}
    </Status>
  )
}

function WebTile({ state, range }: { state: WebState; range: Range }) {
  const body = JSON.stringify(webBody(state, range))
  const q = useQuery((signal) => api.web(JSON.parse(body), signal), [body])
  const d = q.data
  const buckets = d?.series.map((s) => s.bucket) ?? []
  const annotations = useAnnotations(buckets, d?.interval ?? 'day')
  return (
    <Status state={q} empty={webEmpty(d)}>
      {d && (
        <>
          <div className="stats">
            <StatTile label="Visitors" value={num(d.current.visitors)} hint={`${num(d.current.pageviews)} pageviews`} />
            <StatTile label="Sessions" value={num(d.current.sessions)} hint={`${num(d.current.bounces)} bounced`} />
            <StatTile label="Bounce rate" value={d.current.bounce_rate === null ? '—' : pct(d.current.bounce_rate)} hint="of sessions" />
          </div>
          <TimeChart buckets={buckets} interval={d.interval} mode="line" height={180} notes={annotations.notes} series={[
            { key: 'visitors', label: 'Visitors', colorIndex: 0, values: d.series.map((s) => s.visitors) },
            { key: 'pageviews', label: 'Pageviews', colorIndex: 1, values: d.series.map((s) => s.pageviews) },
          ]} />
        </>
      )}
    </Status>
  )
}
