import type { ProblemDetails } from '@/types/api'

const TOKEN_KEY = 'mx-access-token'

/** Error carrying the server's RFC 9457 problem details. */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(problem: ProblemDetails) {
    super(problem.detail ?? problem.title)
    this.status = problem.status
    this.problem = problem
  }
}

// The token lives in sessionStorage: it does not survive closing the tab and is never
// shared across tabs. A production deployment may move to an httpOnly cookie.
export const tokenStore = {
  get(): string | null {
    try {
      return sessionStorage.getItem(TOKEN_KEY)
    } catch {
      return null
    }
  },
  set(token: string | null) {
    try {
      if (token) sessionStorage.setItem(TOKEN_KEY, token)
      else sessionStorage.removeItem(TOKEN_KEY)
    } catch {
      /* storage unavailable: session lasts for this page only */
    }
  },
}

const ORG_KEY = 'mx-active-org'

/**
 * Organization a platform administrator is currently managing. Sent as X-MX-Organization; the
 * server only honours it for platform administrators (everyone else is fixed to their own).
 */
export const activeOrgStore = {
  get(): string | null {
    try {
      return sessionStorage.getItem(ORG_KEY)
    } catch {
      return null
    }
  },
  set(id: string | null) {
    try {
      if (id) sessionStorage.setItem(ORG_KEY, id)
      else sessionStorage.removeItem(ORG_KEY)
    } catch {
      /* storage unavailable */
    }
  },
}

let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(handler: (() => void) | null) {
  onUnauthorized = handler
}

type Query = Record<string, string | number | boolean | null | undefined>

function buildUrl(path: string, query?: Query): string {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value))
  }
  const qs = params.toString()
  return `/api${path}${qs ? `?${qs}` : ''}`
}

async function request<T>(method: string, path: string, options: { query?: Query; body?: unknown; signal?: AbortSignal; platformScope?: boolean } = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  const token = tokenStore.get()
  if (token) headers.Authorization = `Bearer ${token}`
  const org = activeOrgStore.get()
  if (org && !options.platformScope) headers['X-MX-Organization'] = org
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'

  const res = await fetch(buildUrl(path, options.query), {
    method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  })

  if (res.status === 401 && token) onUnauthorized?.()
  if (!res.ok) {
    let problem: ProblemDetails = { status: res.status, title: res.statusText || 'Request failed' }
    try {
      problem = { ...problem, ...(await res.json()) }
    } catch {
      /* non-JSON error body */
    }
    if (res.status === 403 && !problem.detail) problem.detail = 'You do not have permission to perform this action.'
    throw new ApiError(problem)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const api = {
  get: <T>(path: string, query?: Query, signal?: AbortSignal) => request<T>('GET', path, { query, signal }),
  /** GET without the organization header (platform-wide view for platform administrators). */
  getPlatform: <T>(path: string, query?: Query) => request<T>('GET', path, { query, platformScope: true }),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, { body }),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, { body }),
  delete: <T = void>(path: string) => request<T>('DELETE', path),
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return 'Something went wrong.'
}
