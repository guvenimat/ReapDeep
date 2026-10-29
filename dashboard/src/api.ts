export type Scope = 'event' | 'person'
export type Op = 'eq' | 'neq' | 'contains' | 'not_contains' | 'in' | 'is_set' | 'is_not_set'
export type Metric = 'total' | 'unique_users'
export type Interval = 'hour' | 'day' | 'week' | 'month'

export interface Filter { scope: Scope; property: string; op: Op; value?: string }
export interface Breakdown { scope: Scope; property: string; limit?: number }
export interface Series { event: string; metric: Metric }

export interface TrendResult { event: string; metric: Metric; breakdown_value: string | null; total: number; data: { bucket: string; count: number }[] }
export interface TrendResponse { interval: Interval; timezone: string; results: TrendResult[]; took_ms: number }

export interface FunnelStepResult { step: number; event: string; count: number; conversion_from_first: number; conversion_from_previous: number }
export interface FunnelResponse { window_seconds: number; sampling: number; results: { breakdown_value: string | null; steps: FunnelStepResult[] }[]; took_ms: number }

export interface EventRow {
  event_id: string; event: string; timestamp: string; distinct_id: string; resolved_person_id: string
  columns: Record<string, string>; properties: Record<string, unknown> | null
}
export interface Person { id: string; is_identified: boolean; created_at: string; distinct_ids: string[]; properties: Record<string, unknown> }
export interface Metadata {
  events: { name: string; count: number }[]
  event_columns: string[]
  event_properties: { name: string; count: number }[]
  person_properties: { name: string; count: number }[]
}

export interface User { id: number; email: string; name: string }
export interface Project { id: number; name: string; organization_id: number; organization_name: string; role: 'owner' | 'admin' | 'member'; timezone: string }
export interface Me { user: User; projects: Project[] }
export interface DeletionRequest {
  id: number; target_type: 'person' | 'distinct_id'; target_id: string; requested_by: string | null
  requested_at: string; processed_at: string | null
}

const PROJECT_STORAGE = 'reapdeep.projectId'
let currentProject: number | undefined
let onUnauthorized: () => void = () => {}

export function storedProjectId(): number | undefined {
  try {
    const v = Number(localStorage.getItem(PROJECT_STORAGE))
    return Number.isInteger(v) && v > 0 ? v : undefined
  } catch {
    return undefined
  }
}

export function setProject(id: number) {
  currentProject = id
  try {
    localStorage.setItem(PROJECT_STORAGE, String(id))
  } catch {
  }
}

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

async function request<T>(method: 'GET' | 'POST', path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method,
    signal,
    credentials: 'same-origin',
    headers: {
      ...(currentProject !== undefined ? { 'X-Project-Id': String(currentProject) } : {}),
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  })
  if (res.status === 401 && !path.startsWith('/api/v1/auth/')) onUnauthorized()
  if (!res.ok) {
    let msg = `HTTP ${res.status}`
    try {
      msg = ((await res.json()) as { error?: string }).error ?? msg
    } catch {
    }
    throw new ApiError(res.status, msg)
  }
  return (res.status === 204 ? undefined : await res.json()) as T
}

const qs = (params: object) => {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== '') p.set(k, String(v))
  return p.toString()
}

export interface DateRange { date_from?: string; date_to?: string }

export const api = {
  health: () => fetch('/healthz').then((r) => r.ok),
  me: () => request<Me>('GET', '/api/v1/auth/me'),
  login: (email: string, password: string) => request<Me>('POST', '/api/v1/auth/login', { email, password }),
  logout: () => request<void>('POST', '/api/v1/auth/logout'),
  metadata: (signal?: AbortSignal) => request<Metadata>('GET', '/api/v1/metadata', undefined, signal),
  trends: (body: { series: Series[]; interval: Interval; filters: Filter[]; breakdown?: Breakdown; deduplicate: boolean } & DateRange, signal?: AbortSignal) =>
    request<TrendResponse>('POST', '/api/v1/query/trends', body, signal),
  funnels: (body: { steps: { event: string }[]; window_seconds: number; filters: Filter[]; breakdown?: Breakdown; sampling: number } & DateRange, signal?: AbortSignal) =>
    request<FunnelResponse>('POST', '/api/v1/query/funnels', body, signal),
  events: (params: { event?: string; distinct_id?: string; person_id?: string; limit?: number } & DateRange, signal?: AbortSignal) =>
    request<{ events: EventRow[]; took_ms: number }>('GET', `/api/v1/events?${qs(params)}`, undefined, signal),
  persons: (params: { search?: string; cursor?: string; limit?: number; identified?: boolean }, signal?: AbortSignal) =>
    request<{ persons: Person[]; next_cursor: string | null }>('GET', `/api/v1/persons?${qs(params)}`, undefined, signal),
  person: (id: string, signal?: AbortSignal) => request<Person>('GET', `/api/v1/persons/${encodeURIComponent(id)}`, undefined, signal),
  deletionRequests: (signal?: AbortSignal) => request<{ deletion_requests: DeletionRequest[] }>('GET', '/api/v1/deletion-requests', undefined, signal),
  requestDeletion: (target_type: DeletionRequest['target_type'], target_id: string) =>
    request<DeletionRequest>('POST', '/api/v1/deletion-requests', { target_type, target_id }),
}
