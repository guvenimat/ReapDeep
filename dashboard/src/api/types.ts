export type Scope = 'event' | 'person' | 'cohort'
export type Op = 'eq' | 'neq' | 'contains' | 'not_contains' | 'in' | 'not_in' | 'is_set' | 'is_not_set'
export type PropertyMetric = 'sum' | 'avg' | 'min' | 'max' | 'median' | 'p90'
export type Metric = 'total' | 'unique_users' | PropertyMetric
export type Interval = 'hour' | 'day' | 'week' | 'month'

export interface Filter { scope: Scope; property: string; op: Op; value?: string }
export interface Breakdown { scope: Scope; property: string; limit?: number; cohort_ids?: number[] }
export interface Series { event: string; metric: Metric; property?: string }
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
export interface RfmPerson {
  window_days: number; segment: RfmSegmentKey | null; segment_name: string | null; r: number | null; f: number | null
  recency_days: number | null; orders: number | null; monetary: number | null
}
export type LifecycleStatus = 'new' | 'returning' | 'resurrecting' | 'dormant'
export interface LifecycleResponse {
  event: string | null; period: 'day' | 'week' | 'month'; lookback_days: number; timezone: string
  periods: ({ start: string } & Record<LifecycleStatus, number>)[]; took_ms: number
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
export interface Me { user: User; projects: Project[] }
export type Role = Project['role']
export interface Member { user_id: number; email: string; name: string; role: Role; created_at: string }
export type KeyScope = 'read' | 'catalog_write'
export interface ApiKey { id: number; name: string; prefix: string; scope: KeyScope; created_by: string | null; created_at: string; revoked_at: string | null }

export type InsightKind = 'trends' | 'funnels' | 'retention' | 'paths' | 'rfm' | 'lifecycle'
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
export type CohortCondition =
  | { type: 'performed_event'; event: string; count_op: CountOp; count: number; within_days: number; filters?: Filter[] }
  | { type: 'not_performed_event'; event: string; within_days: number }
  | { type: 'person_property'; property: string; op: Op; value?: string }
  | { type: 'rfm_segment'; segments: RfmSegmentKey[]; within_days: number; event?: string }
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
export type RecommendationModel = 'popular' | 'trending' | 'co_viewed' | 'bought_together'
export interface ModelRun { model: RecommendationModel; calculated_at: string; calculation_ms: number; source_items: number }
export interface HitRate {
  k: number; cases: number; covered: number; co_viewed_hits: number; co_viewed_hit_rate: number; popular_hits: number; popular_hit_rate: number
  train_from: string; test_from: string; test_to: string; took_ms: number
}
