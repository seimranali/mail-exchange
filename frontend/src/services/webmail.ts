import type { AuthCheckResult, ProblemDetails } from '@/types/api'
import { ApiError } from './apiClient'

/*
 * Webmail client for mailbox users. Separate from the admin client: its own token (a webmail token
 * is rejected by the admin API and vice versa) and no organization header.
 */

export type WebmailFolderName = 'Inbox' | 'Sent' | 'Drafts' | 'Junk' | 'Trash' | 'Archive'

export interface WebmailProfile { address: string; displayName: string; aliases: string[]; quotaBytes: number; usedBytes: number }
export interface WebmailFolder { folder: WebmailFolderName; total: number; unread: number }
export interface WebmailAddress { name: string | null; address: string }
export interface WebmailListItem {
  id: string
  folder: WebmailFolderName
  from: WebmailAddress | null
  to: WebmailAddress[]
  subject: string | null
  snippet: string
  date: string
  size: number
  isRead: boolean
  isFlagged: boolean
  isAnswered: boolean
  hasAttachments: boolean
}
export interface WebmailList { items: WebmailListItem[]; total: number; page: number; pageSize: number }
export interface WebmailAttachment { index: number; fileName: string; contentType: string; size: number }
export interface WebmailMessage {
  id: string
  folder: WebmailFolderName
  from: WebmailAddress | null
  to: WebmailAddress[]
  cc: WebmailAddress[]
  replyTo: WebmailAddress[]
  subject: string | null
  date: string
  html: string | null
  text: string | null
  attachments: WebmailAttachment[]
  isRead: boolean
  isFlagged: boolean
  isAnswered: boolean
  messageId: string | null
  receivedOverTls: boolean
  spf: AuthCheckResult
  dkim: AuthCheckResult
  dmarc: AuthCheckResult
  spamScore: number | null
}
export interface WebmailLoginResponse { accessToken: string; expiresAt: string; profile: WebmailProfile }

export interface SendMessage {
  to: string
  cc?: string
  bcc?: string
  subject?: string
  text?: string
  html?: string
  inReplyTo?: string
  from?: string
  attachments?: File[]
}

const TOKEN_KEY = 'mx-webmail-token'

export const webmailToken = {
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

let onSignedOut: (() => void) | null = null
export function setWebmailSignedOutHandler(handler: (() => void) | null) {
  onSignedOut = handler
}

async function request(method: string, path: string, body?: unknown): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  const token = webmailToken.get()
  if (token) headers.Authorization = `Bearer ${token}`
  let payload: BodyInit | undefined
  if (body instanceof FormData) payload = body
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }
  const res = await fetch(`/api/webmail${path}`, { method, headers, body: payload })
  if (res.status === 401 && token) {
    webmailToken.set(null)
    onSignedOut?.()
  }
  if (!res.ok) {
    let problem: ProblemDetails = { status: res.status, title: res.statusText || 'Request failed' }
    try {
      problem = { ...problem, ...(await res.json()) }
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(problem)
  }
  return res
}

const json = async <T>(method: string, path: string, body?: unknown): Promise<T> => {
  const res = await request(method, path, body)
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
}

export const webmailApi = {
  login: (email: string, password: string) => json<WebmailLoginResponse>('POST', '/login', { email, password }),
  me: () => json<WebmailProfile>('GET', '/me'),
  folders: () => json<WebmailFolder[]>('GET', '/folders'),
  list: (q: { folder: WebmailFolderName; search?: string; unreadOnly?: boolean; page?: number; pageSize?: number }) => {
    const params = new URLSearchParams({ folder: q.folder, page: String(q.page ?? 1), pageSize: String(q.pageSize ?? 50) })
    if (q.search) params.set('search', q.search)
    if (q.unreadOnly) params.set('unreadOnly', 'true')
    return json<WebmailList>('GET', `/messages?${params}`)
  },
  get: (id: string) => json<WebmailMessage>('GET', `/messages/${id}`),
  setFlags: (ids: string[], flags: { isRead?: boolean; isFlagged?: boolean }) => json<void>('POST', '/messages/flags', { ids, ...flags }),
  move: (ids: string[], folder: WebmailFolderName) => json<void>('POST', '/messages/move', { ids, folder }),
  remove: (ids: string[]) => json<void>('POST', '/messages/delete', { ids }),
  send: (m: SendMessage) => {
    const form = new FormData()
    form.set('to', m.to)
    if (m.cc) form.set('cc', m.cc)
    if (m.bcc) form.set('bcc', m.bcc)
    form.set('subject', m.subject ?? '')
    if (m.text !== undefined) form.set('text', m.text)
    if (m.html) form.set('html', m.html)
    if (m.inReplyTo) form.set('inReplyTo', m.inReplyTo)
    if (m.from) form.set('from', m.from)
    for (const file of m.attachments ?? []) form.append('attachments', file, file.name)
    return json<{ messageId: string }>('POST', '/send', form)
  },
  /** Downloads an attachment (the API needs the bearer token, so a plain link can't be used). */
  async download(messageId: string, attachment: WebmailAttachment) {
    const res = await request('GET', `/messages/${messageId}/attachments/${attachment.index}`)
    const url = URL.createObjectURL(await res.blob())
    const a = document.createElement('a')
    a.href = url
    a.download = attachment.fileName
    document.body.appendChild(a)
    a.click()
    a.remove()
    setTimeout(() => URL.revokeObjectURL(url), 10_000)
  },
}
