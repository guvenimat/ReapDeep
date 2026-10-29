export type Scope = 'event' | 'person'
export type Op = 'eq' | 'neq' | 'contains' | 'not_contains' | 'in' | 'is_set' | 'is_not_set'
export type Metric = 'total' | 'unique_users'
export type Interval = 'hour' | 'day' | 'week' | 'month'

export interface Filter { scope: Scope; property: string; op: Op; value?: string }
export interface Breakdown { scope: Scope; property: string; limit?: number }
export interface Series { event: string; metric: Metric }
export interface DateRange { date_from?: string; date_to?: string }

export interface TrendResult { event: string; metric: Metric; breakdown_value: string | null; total: number; data: { bucket: string; count: number }[] }
export interface TrendResponse { interval: Interval; timezone: string; results: TrendResult[]; took_ms: number }
export interface TrendRequest extends DateRange { series: Series[]; interval: Interval; filters: Filter[]; breakdown?: Breakdown; deduplicate: boolean }

export interface FunnelStepResult { step: number; event: string; count: number; conversion_from_first: number; conversion_from_previous: number }
export interface FunnelResponse { window_seconds: number; sampling: number; results: { breakdown_value: string | null; steps: FunnelStepResult[] }[]; took_ms: number }
export interface FunnelRequest extends DateRange { steps: { event: string }[]; window_seconds: number; filters: Filter[]; breakdown?: Breakdown; sampling: number }

export interface RetentionResponse {
  period: 'day' | 'week'; periods: number; timezone: string
  cohorts: { cohort_start: string; size: number; counts: (number | null)[] }[]
  average: (number | null)[]; took_ms: number
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
export interface ApiKey { id: number; name: string; prefix: string; created_by: string | null; created_at: string; revoked_at: string | null }

export type InsightKind = 'trends' | 'funnels' | 'retention' | 'paths'
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
