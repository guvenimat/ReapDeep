import { api } from '../../api/client'
import type { Insight } from '../../api/types'
import type { Range } from '../../app/route'
import { Legend } from '../../components/charts/common'
import { FunnelChart } from '../../components/charts/FunnelChart'
import { TimeChart } from '../../components/charts/TimeChart'
import { Status } from '../../components/ui'
import { num, pct } from '../../lib/format'
import { useQuery } from '../../lib/hooks'
import { LIFECYCLE_DEFAULT, lifecycleBody, lifecycleEmpty, LifecycleLegend, type LifecycleState } from '../lifecycle/Lifecycle'
import { LifecycleChart } from '../../components/charts/LifecycleChart'
import { RFM_DEFAULT, rfmBody, RfmGrid, type RfmState } from '../rfm/Rfm'
import { FUNNEL_DEFAULT, funnelBody, funnelGroups, type FunnelState } from '../funnels/Funnels'
import { PathList, PATHS_DEFAULT, pathsBody, type PathsState } from '../paths/Paths'
import { RETENTION_DEFAULT, retentionBody, RetentionTable, type RetentionState } from '../retention/Retention'
import { TREND_DEFAULT, trendBody, trendChartSeries, trendInterval, type TrendState } from '../trends/Trends'

export function InsightTile({ insight, range }: { insight: Insight; range: Range }) {
  if (insight.kind === 'trends') return <TrendTile state={{ ...TREND_DEFAULT, ...(insight.query as Partial<TrendState>) }} range={range} />
  if (insight.kind === 'retention') return <RetentionTile state={{ ...RETENTION_DEFAULT, ...(insight.query as Partial<RetentionState>) }} range={range} />
  if (insight.kind === 'lifecycle') return <LifecycleTile state={{ ...LIFECYCLE_DEFAULT, ...(insight.query as Partial<LifecycleState>) }} range={range} />
  if (insight.kind === 'rfm') return <RfmTile state={{ ...RFM_DEFAULT, ...(insight.query as Partial<RfmState>) }} />
  if (insight.kind === 'paths') return <PathsTile state={{ ...PATHS_DEFAULT, ...(insight.query as Partial<PathsState>) }} range={range} />
  return <FunnelTile state={{ ...FUNNEL_DEFAULT, ...(insight.query as Partial<FunnelState>) }} range={range} />
}

function TrendTile({ state, range }: { state: TrendState; range: Range }) {
  const body = JSON.stringify(trendBody(state, range))
  const q = useQuery((signal) => api.trends(JSON.parse(body), signal), [body])
  const series = trendChartSeries(q.data)
  const buckets = q.data?.results[0]?.data.map((d) => d.bucket) ?? []
  const total = q.data?.results.length === 1 ? q.data.results[0].total : undefined
  return (
    <Status state={q} empty={(q.data?.results ?? []).every((r) => r.total === 0)}>
      {total !== undefined && <div className="tile-total" data-testid="tile-total">{num(total)}</div>}
      <Legend items={series.map((s) => ({ label: s.label, colorIndex: s.colorIndex }))} />
      <TimeChart buckets={buckets} interval={trendInterval(state, range)} series={series} mode={state.chart} height={220} />
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
