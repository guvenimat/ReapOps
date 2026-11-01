export type Severity = 'info' | 'warning' | 'critical'
export type Status = 'open' | 'acknowledged' | 'resolved'

export type IncidentSummary = {
  id: string; fingerprint: string; service: string; title: string; severity: Severity; status: Status
  opened_at: string; last_seen_at: string; resolved_at: string | null
}
export type Evidence = { label: string; value: string; query: string | null }
export type TimelineEntry = { kind: string; detail: string; at: string }
export type Diagnosis = {
  id: string; status: 'completed' | 'escalated' | 'failed'; mode: 'off' | 'shadow' | 'visible'; model: string; prompt_version: string
  summary: string; root_cause: string; confidence: 'low' | 'medium' | 'high'
  evidence: { statement: string; sources: string[] }[]
  alternatives: { hypothesis: string; why: string }[]
  options: { action_id: string; parameters: Record<string, string>; rationale: string }[]
  notes: string[]; tool_calls: number; input_tokens: number; output_tokens: number; created_at: string
}
export type IncidentDetail = {
  summary: IncidentSummary; evidence: Evidence[]; acknowledged_by: string | null; acknowledged_at: string | null
  resolution: string | null; timeline: TimelineEntry[]; diagnoses: Diagnosis[]
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api${path}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
  })
  if (res.status === 204) return undefined as T
  const data = await res.json().catch(() => null)
  if (!res.ok) throw new ApiError(res.status, data?.error ?? 'Something went wrong')
  return data as T
}

export const api = {
  me: () => call<{ name: string }>('GET', '/me'),
  login: (name: string, password: string) => call<{ name: string }>('POST', '/auth/login', { name, password }),
  logout: () => call<void>('POST', '/auth/logout'),
  incidents: (status: string) => call<{ incidents: IncidentSummary[] }>('GET', `/incidents${status ? `?status=${status}` : ''}`),
  incident: (id: string) => call<IncidentDetail>('GET', `/incidents/${id}`),
  acknowledge: (id: string) => call<void>('POST', `/incidents/${id}/acknowledge`),
  resolve: (id: string) => call<void>('POST', `/incidents/${id}/resolve`),
}
