import type {
  ApiKey, CatalogItem, CatalogItemDetail, CatalogItemInput, Cohort, CohortDefinition, CohortKind, CohortPerson, Dashboard, DashboardSummary, DateRange, DeletionRequest, EventRow, FunnelRequest, FunnelResponse, Insight, InsightKind,
  HitRate, KeyScope, Me, Member, Metadata, ModelRun, LifecycleResponse, PathsResponse, Person, RetentionResponse, RfmResponse, Role, TrendRequest, TrendResponse,
} from './types'

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

export const errorMessage = (err: unknown) => (err instanceof Error ? err.message : String(err))

async function request<T>(method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE', path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
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

export const api = {
  me: () => request<Me>('GET', '/api/v1/auth/me'),
  login: (email: string, password: string) => request<Me>('POST', '/api/v1/auth/login', { email, password }),
  logout: () => request<void>('POST', '/api/v1/auth/logout'),
  changePassword: (current_password: string, new_password: string) =>
    request<void>('POST', '/api/v1/auth/password', { current_password, new_password }),

  metadata: (signal?: AbortSignal) => request<Metadata>('GET', '/api/v1/metadata', undefined, signal),

  trends: (body: TrendRequest, signal?: AbortSignal) => request<TrendResponse>('POST', '/api/v1/query/trends', body, signal),
  funnels: (body: FunnelRequest, signal?: AbortSignal) => request<FunnelResponse>('POST', '/api/v1/query/funnels', body, signal),
  retention: (body: object, signal?: AbortSignal) => request<RetentionResponse>('POST', '/api/v1/query/retention', body, signal),
  paths: (body: object, signal?: AbortSignal) => request<PathsResponse>('POST', '/api/v1/query/paths', body, signal),
  rfm: (body: object, signal?: AbortSignal) => request<RfmResponse>('POST', '/api/v1/query/rfm', body, signal),
  lifecycle: (body: object, signal?: AbortSignal) => request<LifecycleResponse>('POST', '/api/v1/query/lifecycle', body, signal),

  events: (params: { event?: string; distinct_id?: string; person_id?: string; limit?: number } & DateRange, signal?: AbortSignal) =>
    request<{ events: EventRow[]; took_ms: number }>('GET', `/api/v1/events?${qs(params)}`, undefined, signal),
  persons: (params: { search?: string; cursor?: string; limit?: number; identified?: boolean }, signal?: AbortSignal) =>
    request<{ persons: Person[]; next_cursor: string | null }>('GET', `/api/v1/persons?${qs(params)}`, undefined, signal),
  person: (id: string, signal?: AbortSignal) => request<Person>('GET', `/api/v1/persons/${encodeURIComponent(id)}`, undefined, signal),

  members: (signal?: AbortSignal) => request<{ members: Member[] }>('GET', '/api/v1/members', undefined, signal),
  addMember: (body: { email: string; name?: string; password?: string; role: Role }) => request<Member>('POST', '/api/v1/members', body),
  changeRole: (userId: number, role: Role) => request<Member>('PATCH', `/api/v1/members/${userId}`, { role }),
  removeMember: (userId: number) => request<void>('DELETE', `/api/v1/members/${userId}`),
  apiKeys: (signal?: AbortSignal) => request<{ api_keys: ApiKey[] }>('GET', '/api/v1/api-keys', undefined, signal),
  createApiKey: (name: string, scope: KeyScope) => request<{ api_key: ApiKey; key: string }>('POST', '/api/v1/api-keys', { name, scope }),
  revokeApiKey: (id: number) => request<void>('DELETE', `/api/v1/api-keys/${id}`),

  insights: (signal?: AbortSignal) => request<{ insights: Insight[] }>('GET', '/api/v1/insights', undefined, signal),
  createInsight: (body: { name: string; kind: InsightKind; query: object }) => request<Insight>('POST', '/api/v1/insights', body),
  updateInsight: (id: number, body: { name?: string; query?: object }) => request<Insight>('PUT', `/api/v1/insights/${id}`, body),
  deleteInsight: (id: number) => request<void>('DELETE', `/api/v1/insights/${id}`),
  dashboards: (signal?: AbortSignal) => request<{ dashboards: DashboardSummary[] }>('GET', '/api/v1/dashboards', undefined, signal),
  dashboard: (id: number, signal?: AbortSignal) => request<Dashboard>('GET', `/api/v1/dashboards/${id}`, undefined, signal),
  createDashboard: (body: { name: string; description?: string; insight_ids?: number[] }) => request<Dashboard>('POST', '/api/v1/dashboards', body),
  updateDashboard: (id: number, body: { name?: string; description?: string; insight_ids?: number[] }) =>
    request<Dashboard>('PUT', `/api/v1/dashboards/${id}`, body),
  deleteDashboard: (id: number) => request<void>('DELETE', `/api/v1/dashboards/${id}`),

  cohorts: (signal?: AbortSignal) => request<{ cohorts: Cohort[] }>('GET', '/api/v1/cohorts', undefined, signal),
  cohort: (id: number, signal?: AbortSignal) => request<Cohort>('GET', `/api/v1/cohorts/${id}`, undefined, signal),
  createCohort: (body: { name: string; description?: string; kind: CohortKind; definition?: CohortDefinition; members?: string[] }) =>
    request<Cohort>('POST', '/api/v1/cohorts', body),
  updateCohort: (id: number, body: { name?: string; description?: string; definition?: CohortDefinition }) =>
    request<Cohort>('PUT', `/api/v1/cohorts/${id}`, body),
  replaceCohortMembers: (id: number, members: string[]) => request<Cohort>('PUT', `/api/v1/cohorts/${id}/members`, { members }),
  recalculateCohort: (id: number) => request<Cohort>('POST', `/api/v1/cohorts/${id}/recalculate`),
  deleteCohort: (id: number) => request<void>('DELETE', `/api/v1/cohorts/${id}`),
  cohortUsage: (id: number, signal?: AbortSignal) =>
    request<{ insights: { id: number; name: string; kind: string }[] }>('GET', `/api/v1/cohorts/${id}/usage`, undefined, signal),
  cohortPersons: (id: number, cursor?: string, signal?: AbortSignal) =>
    request<{ persons: CohortPerson[]; next_cursor: string | null }>('GET', `/api/v1/cohorts/${id}/persons?${qs({ cursor, limit: 50 })}`, undefined, signal),
  previewCohort: (definition: CohortDefinition, signal?: AbortSignal) =>
    request<{ persons: number; took_ms: number }>('POST', '/api/v1/cohorts/preview', { definition }, signal),

  items: (params: { search?: string; category?: string; cursor?: string; limit?: number }, signal?: AbortSignal) =>
    request<{ items: CatalogItem[]; next_cursor: string | null; total: number; in_stock: number; categories: string[] }>(
      'GET', `/api/v1/items?${qs(params)}`, undefined, signal),
  item: (itemId: string, signal?: AbortSignal) => request<CatalogItemDetail>('GET', `/api/v1/items/${encodeURIComponent(itemId)}`, undefined, signal),
  upsertItems: (items: CatalogItemInput[]) => request<{ upserted: number }>('PUT', '/api/v1/items', { items }),
  recommendationModels: (signal?: AbortSignal) => request<{ models: ModelRun[] }>('GET', '/api/v1/recommendations/models', undefined, signal),
  evaluateRecommendations: (k = 10) => request<HitRate>('POST', `/api/v1/recommendations/evaluate?k=${k}`),

  deletionRequests: (signal?: AbortSignal) => request<{ deletion_requests: DeletionRequest[] }>('GET', '/api/v1/deletion-requests', undefined, signal),
  requestDeletion: (target_type: DeletionRequest['target_type'], target_id: string) =>
    request<DeletionRequest>('POST', '/api/v1/deletion-requests', { target_type, target_id }),
}
