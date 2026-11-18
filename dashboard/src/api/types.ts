export type Scope = 'event' | 'person' | 'cohort'
export type Op = 'eq' | 'neq' | 'contains' | 'not_contains' | 'in' | 'not_in' | 'is_set' | 'is_not_set'
export type PropertyMetric = 'sum' | 'avg' | 'min' | 'max' | 'median' | 'p90'
export type Metric = 'total' | 'unique_users' | PropertyMetric
export type Interval = 'hour' | 'day' | 'week' | 'month'

export interface Filter { scope: Scope; property: string; op: Op; value?: string }
export interface Breakdown { scope: Scope; property: string; limit?: number; cohort_ids?: number[] }
export interface Series { event: string; metric: Metric; property?: string }
export interface ActionDef { id: number; name: string; description: string; events: string[]; created_by: string | null; updated_at: string }
export interface DateRange { date_from?: string; date_to?: string }

export interface TrendResult {
  event: string; metric: Metric | 'formula'; breakdown_value: string | null; total: number; data: { bucket: string; count: number }[]
  property: string | null; compare: 'previous' | null; series: string | null
}
export interface TrendResponse { interval: Interval; timezone: string; results: TrendResult[]; took_ms: number }
export interface TrendRequest extends DateRange {
  series: Series[]; interval: Interval; filters: Filter[]; breakdown?: Breakdown; deduplicate: boolean; formula?: string; compare?: boolean
}

export interface FunnelStepResult { step: number; event: string; count: number; conversion_from_first: number; conversion_from_previous: number }
export interface FunnelTimes { steps: { step: number; persons: number; avg_seconds: number | null; median_seconds: number | null }[]; avg_seconds: number | null; median_seconds: number | null }
export interface FunnelResponse {
  window_seconds: number; sampling: number; results: { breakdown_value: string | null; steps: FunnelStepResult[] }[]; took_ms: number; times: FunnelTimes | null
}
export interface FunnelPersonsResponse { step: number; kind: 'dropped' | 'converted'; total: number; persons: CohortPerson[]; took_ms: number }
export interface FunnelRequest extends DateRange { steps: { event: string }[]; window_seconds: number; filters: Filter[]; breakdown?: Breakdown; sampling: number }

export interface RetentionResponse {
  period: 'day' | 'week'; periods: number; timezone: string
  cohorts: { cohort_start: string; size: number; counts: (number | null)[] }[]
  average: (number | null)[]; took_ms: number
}
export type RfmSegmentKey =
  | 'champions' | 'loyal' | 'potential_loyalist' | 'new_customers' | 'promising' | 'need_attention'
  | 'about_to_sleep' | 'at_risk' | 'cant_lose' | 'hibernating' | 'lost'
export interface RfmSegmentResult {
  key: RfmSegmentKey; name: string; description: string; persons: number; share: number
  avg_recency_days: number | null; avg_frequency: number | null; avg_monetary: number | null; revenue: number; revenue_share: number
}
export interface RfmScoreRange { score: number; min: number | null; max: number | null }
export interface RfmResponse {
  window_days: number; purchase_event: string; amount_property: string; calculated_at: string; customers: number; revenue: number
  currencies: string[]; grid: { r: number; f: number; persons: number; segment: RfmSegmentKey }[]; segments: RfmSegmentResult[]
  thresholds: { recency: RfmScoreRange[]; frequency: RfmScoreRange[] }; took_ms: number
}
export interface RfmMigrationResponse {
  window_days: number; period_days: number; from_date: string; to_date: string
  rows: { from: RfmSegmentKey | 'none'; to: RfmSegmentKey | 'none'; persons: number }[]; took_ms: number
}
export interface PersonCohort {
  id: number; name: string; kind: 'dynamic' | 'static'; calculated_at: string | null
}

export interface PersonCohortsResponse {
  cohorts: PersonCohort[]
}

export interface RfmPerson {
  window_days: number; segment: RfmSegmentKey | null; segment_name: string | null; r: number | null; f: number | null
  recency_days: number | null; orders: number | null; monetary: number | null
}
export type LifecycleStatus = 'new' | 'returning' | 'resurrecting' | 'dormant'
export interface LifecycleResponse {
  event: string | null; period: 'day' | 'week' | 'month'; lookback_days: number; timezone: string
  periods: ({ start: string } & Record<LifecycleStatus, number>)[]; took_ms: number
}
export type WebDimension = 'entry_page' | 'referring_domain' | 'utm_source' | 'utm_medium' | 'utm_campaign' | 'device_type' | 'browser' | 'os'
export interface WebSummary {
  visitors: number; pageviews: number; sessions: number; bounces: number; bounce_rate: number | null; avg_session_seconds: number | null
}
export interface WebBreakdownRow { value: string | null; sessions: number; visitors: number; bounce_rate: number; avg_session_seconds: number }
export interface WebAnalyticsResponse {
  interval: Interval; timezone: string; date_from: string; date_to: string; previous_from: string
  current: WebSummary; previous: WebSummary
  series: { bucket: string; visitors: number; pageviews: number }[]
  pages: { pathname: string | null; pageviews: number; visitors: number }[]
  breakdowns: { dimension: WebDimension; rows: WebBreakdownRow[] }[]
  took_ms: number
}
export interface PathsResponse {
  start_event: string; depth: number; persons: number
  steps: { step: number; persons: number; nodes: { name: string; persons: number }[] }[]
  paths: { nodes: string[]; persons: number }[]; took_ms: number
}

export interface EventRow {
  event_id: string; event: string; timestamp: string; distinct_id: string; resolved_person_id: string
  columns: Record<string, string>; properties: Record<string, unknown> | null
}
export interface Person { id: string; is_identified: boolean; created_at: string; distinct_ids: string[]; properties: Record<string, unknown> }

export type PropertyType = 'string' | 'number' | 'boolean' | 'datetime'
export interface PropertyDefinition { name: string; type: PropertyType | null; last_seen_at: string }
export interface Metadata {
  events: { name: string; last_seen_at: string }[]
  event_columns: string[]
  event_properties: PropertyDefinition[]
  person_properties: PropertyDefinition[]
}

export interface User { id: number; email: string; name: string }
export interface Project { id: number; name: string; organization_id: number; organization_name: string; role: 'owner' | 'admin' | 'member'; timezone: string }
export interface Me { user: User; projects: Project[]; platform_admin?: boolean; platform_role?: 'admin' | 'viewer' | null }
export type Role = Project['role']
export interface Member { user_id: number; email: string; name: string; role: Role; created_at: string; pending: boolean; invite_link?: string | null }
export interface MemberLink { purpose: 'invite' | 'reset'; link: string; expires_at: string }
export type AuthTokenPurpose = 'invite' | 'reset'
export interface AuthTokenInfo { email: string; purpose: AuthTokenPurpose }
export type KeyScope = 'read' | 'catalog_write' | 'server'
export interface ApiKey { id: number; name: string; prefix: string; scope: KeyScope; created_by: string | null; created_at: string; revoked_at: string | null }

export type InsightKind = 'trends' | 'funnels' | 'retention' | 'paths' | 'rfm' | 'lifecycle'
export interface Annotation { id: number; day: string; content: string; created_by: string | null; created_at: string }
export interface Insight {
  id: number; name: string; kind: InsightKind; query: Record<string, unknown>
  created_by: string | null; created_at: string; updated_at: string
}
export interface DashboardSummary { id: number; name: string; description: string; created_by: string | null; updated_at: string; items: number }
export interface Dashboard { id: number; name: string; description: string; updated_at: string; items: Insight[] }

export interface DeletionRequest {
  id: number; target_type: 'person' | 'distinct_id'; target_id: string; requested_by: string | null
  requested_at: string; processed_at: string | null
}

export type CohortKind = 'dynamic' | 'static'
export type CohortStatus = 'pending' | 'calculating' | 'ready' | 'error'
export type CountOp = 'gte' | 'lte' | 'eq'
export type FunnelOutcome = 'dropped' | 'converted'
export type CohortCondition =
  | { type: 'performed_event'; event: string; count_op: CountOp; count: number; within_days: number; filters?: Filter[] }
  | { type: 'not_performed_event'; event: string; within_days: number }
  | { type: 'person_property'; property: string; op: Op; value?: string }
  | { type: 'rfm_segment'; segments: RfmSegmentKey[]; within_days: number; event?: string }
  | { type: 'funnel'; steps: string[]; outcome: FunnelOutcome; step: number; window_seconds: number; within_days: number }
export interface CohortDefinition { groups: { conditions: CohortCondition[] }[] }
export interface Cohort {
  id: number; name: string; description: string; kind: CohortKind; definition: CohortDefinition | null
  status: CohortStatus; member_count: number | null; static_members: number | null
  calculated_at: string | null; calculation_ms: number | null; last_error: string | null
  created_by: string | null; created_at: string; updated_at: string
}
export interface CohortPerson { id: string; is_identified: boolean; distinct_ids: string[]; properties: Record<string, unknown> }

export interface CatalogItem {
  item_id: string; title: string; category: string; price: number | null; currency: string; in_stock: boolean
  url: string; image_url: string; attributes: Record<string, unknown>; updated_at: string
}
export interface CatalogItemInput {
  item_id: string; title: string; category?: string; price?: number | null; currency?: string; in_stock?: boolean; url?: string; image_url?: string
}
export interface RecommendedCatalogItem { item_id: string; title: string; category: string; in_stock: boolean; score: number }
export interface CatalogItemDetail { item: CatalogItem; co_viewed: RecommendedCatalogItem[]; bought_together: RecommendedCatalogItem[] }
export type RecommendationModel = 'popular' | 'trending' | 'co_viewed' | 'bought_together' | 'co_viewed_weighted' | 'co_viewed_session'
export interface ModelRun { model: RecommendationModel; calculated_at: string; calculation_ms: number; source_items: number }
export interface RecommendationPerformance {
  model: string; placement: string; impressions: number; clicks: number; purchases: number; click_rate: number | null; purchase_rate: number | null
}

export interface RecommendationPerformanceResponse {
  days: number; attribution_days: number; took_ms: number; rows: RecommendationPerformance[]
}

export interface HitRate {
  k: number; cases: number; covered: number; co_viewed_hits: number; co_viewed_hit_rate: number; popular_hits: number; popular_hit_rate: number
  co_viewed_weighted_hits: number; co_viewed_weighted_hit_rate: number; co_viewed_session_hits: number; co_viewed_session_hit_rate: number
  train_from: string; test_from: string; test_to: string; took_ms: number
}

export type AlertCondition = 'above' | 'below' | 'increase' | 'decrease'
export type AlertWindow = 'hour' | 'day' | 'week'
export interface Alert {
  id: number; name: string; insight_id: number; insight_name: string; condition: AlertCondition; threshold: number; window: AlertWindow
  channel: AlertChannel; webhook_url: string | null; email_to: string[] | null; secret: string | null; enabled: boolean; state: 'pending' | 'ok' | 'firing' | 'error'
  last_value: number | null; previous_value: number | null; last_error: string | null; last_checked_at: string | null
  next_check_at: string; created_by: string | null; created_at: string
}
export interface AlertTestInput { alert_id?: number; name?: string; channel: AlertChannel; webhook_url?: string; email_to?: string[] }
export interface AlertTestResult { delivered: boolean; response_status: number | null; error: string | null }
export interface AlertEvent {
  id: number; kind: 'fired' | 'resolved' | 'error'; value: number | null; previous_value: number | null; message: string
  response_status: number | null; delivery_error: string | null
  delivery_status: 'none' | 'pending' | 'delivered' | 'failed'; attempts: number; next_attempt_at: string | null; delivered_at: string | null; created_at: string
}
export type AlertChannel = 'webhook' | 'slack' | 'email'
export interface AlertInput {
  name: string; insight_id: number; condition: AlertCondition; threshold: number; window: AlertWindow; enabled: boolean
  channel: AlertChannel; webhook_url?: string; email_to?: string[]
}

export type FlagOp = 'eq' | 'neq' | 'contains' | 'not_contains' | 'in' | 'not_in' | 'gt' | 'gte' | 'lt' | 'lte' | 'is_set' | 'is_not_set' | 'in_cohort' | 'not_in_cohort'
export type FlagScalar = string | number | boolean
export interface FlagVariant { key: string; percentage: number }
export interface FlagCondition { property: string; op: FlagOp; value?: FlagScalar | FlagScalar[] | null }
export interface FeatureFlag {
  id: number; key: string; name: string; description: string; enabled: boolean; rollout_percentage: number
  variants: FlagVariant[]; conditions: FlagCondition[]; created_by: string | null; updated_by: string | null; created_at: string; updated_at: string
  locked_by_experiment: string | null
}
export interface FlagInput {
  key?: string; name: string; description: string; enabled: boolean; rollout_percentage: number; variants: FlagVariant[]; conditions: FlagCondition[]
}

export type ExperimentStatus = 'draft' | 'running' | 'stopped'
export type MetricKind = 'conversion' | 'funnel' | 'revenue'
export interface ExperimentMetric { kind: MetricKind; event?: string | null; steps?: string[] | null; window_seconds?: number | null; property?: string | null }
export interface Experiment {
  id: number; name: string; description: string; flag_id: number; flag_key: string; flag_enabled: boolean; variants: FlagVariant[]; control_variant: string
  status: ExperimentStatus; metric: ExperimentMetric; start_date: string | null; end_date: string | null
  created_by: string | null; updated_by: string | null; created_at: string; updated_at: string
}
export interface ExperimentInput {
  name: string; description: string; flag_id: number; control_variant: string; metric: ExperimentMetric; start_date?: string; end_date?: string
}
export interface ExperimentComparison {
  difference: number; difference_low: number | null; difference_high: number | null
  relative_lift: number | null; relative_low: number | null; relative_high: number | null
  statistic: number | null; p_value: number | null; degrees_of_freedom: number | null; significant: boolean
}
export interface ExperimentVariantResult {
  key: string; percentage: number; is_control: boolean; persons: number; conversions: number; mean: number; standard_deviation: number | null
  comparison: ExperimentComparison | null
}
export interface ExperimentSrm { chi_square: number; p_value: number; warning: boolean; threshold: number }
export interface ExperimentResults {
  experiment_id: number; metric_kind: MetricKind; test: 'two_proportion_z' | 'welch_t'; control_variant: string; date_from: string; date_to: string
  variants: ExperimentVariantResult[]; excluded_persons: number; srm: ExperimentSrm | null; took_ms: number
}

export type ProjectDetail = {
  id: number; name: string; organization_id: number; organization_name: string; timezone: string; public_key: string; created_at: string
  anonymize_ip: boolean; ingest_events_per_second: number | null
  recommendation_secret?: string | null
  ai_features?: AiFeature[] | null; ai_model?: string | null
}
export type AiFeature = 'experiment_explain' | 'insight_explain' | 'query' | 'digest'
export interface AiAccess { platform_enabled: boolean; allowed: AiFeature[]; enabled: boolean; features: AiFeature[]; effective: AiFeature[]; model: string | null }
export interface PlatformAuditEntry {
  id: number; actor_email: string; organization_id: number | null; organization_name: string; action: string
  before: { enabled: boolean; features: AiFeature[] }; after: { enabled: boolean; features: AiFeature[] }; created_at: string
}
export interface PlatformOrganization {
  id: number; name: string; projects: number; members: number; ai_enabled: boolean; ai_features: AiFeature[]; customer_enabled: boolean
  customer_features: AiFeature[]; effective: AiFeature[]
}
export interface AiVerdict { decision: 'ship' | 'stop' | 'keep_running' | 'no_difference' | 'investigate'; variant: string; reason: string }
export interface AiExperimentIdea { name: string; hypothesis: string; goal_event: string; flag_key: string }
export interface AiAnswer {
  summary: string; findings: string[]; recommendation: string | null; caveats: string[]; next_steps: string[]; model: string; cached: boolean
  generated_at: string; verdict?: AiVerdict | null; experiments?: AiExperimentIdea[] | null
}
export interface AiNote { id: number; trigger: 'manual' | 'schedule'; period_from: string; period_to: string; emailed_to: string[]; created_at: string; answer: AiAnswer }
export type DigestFrequency = 'off' | 'daily' | 'weekly' | 'monthly'
export interface DigestSchedule {
  frequency: DigestFrequency; weekday: number; month_day: number; hour: number; email_to: string[]; next_run_at: string | null
  last_run_at: string | null; last_error: string | null; timezone: string; email_available: boolean
}
export interface AiQueryAnswer { kind: 'trends' | 'funnels'; query: Record<string, unknown>; explanation: string; model: string; cached: boolean }
