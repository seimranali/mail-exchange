import { api } from './apiClient'
import type {
  AlertSeverity, AlertStatus, AuditEvent, DkimKey, AuthenticationEvent, CreateMailboxRequest, CurrentUser, DashboardSummary, Domain,
  LegalHold, LoginResponse, MailCertificate, Mailbox, MailboxStatus, MailboxType, MessageSummary, Organization, PagedResult, QuarantineRecord, QuarantineStatus,
  QueuedMessage, RetentionAction, RetentionPolicy, SecurityAlert, SecurityPolicy, UpdateMailboxRequest,
} from '@/types/api'

export interface PageQuery {
  page?: number
  pageSize?: number
}

export const authApi = {
  login: (email: string, password: string) => api.post<LoginResponse>('/auth/login', { email, password }),
  me: () => api.get<CurrentUser>('/auth/me'),
}

export const systemApi = {
  mailCertificate: () => api.get<MailCertificate>('/system/mail-certificate'),
}

export const dashboardApi = {
  get: () => api.get<DashboardSummary>('/dashboard'),
}

export const organizationsApi = {
  // Always platform-wide: organization admins are limited to their own organization by the server anyway.
  list: (q: PageQuery & { search?: string }) => api.getPlatform<PagedResult<Organization>>('/organizations', { ...q }),
  /** All organizations (platform scope), regardless of the organization being managed. */
  listAll: (q: PageQuery & { search?: string } = {}) => api.getPlatform<PagedResult<Organization>>('/organizations', { pageSize: 200, ...q }),
  create: (body: { name: string; slug: string; plan: string; mailboxLimit: number }) => api.post<Organization>('/organizations', body),
}

export const domainsApi = {
  list: (q: PageQuery = {}) => api.get<PagedResult<Domain>>('/domains', { pageSize: 100, ...q }),
  create: (domainName: string, isDefault: boolean) => api.post<Domain>('/domains', { domainName, isDefault }),
  verify: (id: string) => api.post<Domain>(`/domains/${id}/verify`),
  remove: (id: string) => api.delete(`/domains/${id}`),
  rotateDkim: (id: string) => api.post<DkimKey>(`/domains/${id}/dkim`),
}

export interface MailboxQuery extends PageQuery {
  search?: string
  status?: MailboxStatus
  type?: MailboxType
  domainId?: string
  sort?: string
  desc?: boolean
}

export const mailboxesApi = {
  list: (q: MailboxQuery) => api.get<PagedResult<Mailbox>>('/mailboxes', { ...q }),
  get: (id: string) => api.get<Mailbox>(`/mailboxes/${id}`),
  create: (body: CreateMailboxRequest) => api.post<Mailbox>('/mailboxes', body),
  update: (id: string, body: UpdateMailboxRequest) => api.put<Mailbox>(`/mailboxes/${id}`, body),
  remove: (id: string) => api.delete(`/mailboxes/${id}`),
  addAlias: (id: string, localPart: string, domainId: string) => api.post<Mailbox>(`/mailboxes/${id}/aliases`, { localPart, domainId }),
  setClientPassword: (id: string, password: string | null) => api.put<void>(`/mailboxes/${id}/client-password`, { password }),
  sendTest: (id: string, to: string) => api.post<{ outboundMessageId: string }>(`/mailboxes/${id}/test-message`, { to }),
  messages: (id: string, q: PageQuery = {}) => api.get<PagedResult<MessageSummary>>(`/mailboxes/${id}/messages`, { pageSize: 10, ...q }),
}

export const mailQueueApi = {
  list: (limit = 100, includeFinished = false) => api.get<QueuedMessage[]>('/mail-queue', { limit, includeFinished }),
  retry: (id: string) => api.post<void>(`/mail-queue/${id}/retry`),
  cancel: (id: string) => api.delete(`/mail-queue/${id}`),
}

export const securityApi = {
  alerts: (q: PageQuery & { status?: AlertStatus; severity?: AlertSeverity }) => api.get<PagedResult<SecurityAlert>>('/security/alerts', { ...q }),
  setAlertStatus: (id: string, status: AlertStatus) => api.put<void>(`/security/alerts/${id}/status`, { status }),
  quarantine: (q: PageQuery & { status?: QuarantineStatus; search?: string }) => api.get<PagedResult<QuarantineRecord>>('/security/quarantine', { ...q }),
  release: (id: string) => api.post<void>(`/security/quarantine/${id}/release`),
  deleteQuarantine: (id: string) => api.delete(`/security/quarantine/${id}`),
  policy: () => api.get<SecurityPolicy>('/security/policy'),
  updatePolicy: (body: SecurityPolicy) => api.put<SecurityPolicy>('/security/policy', body),
  signIns: (q: PageQuery) => api.get<PagedResult<AuthenticationEvent>>('/security/sign-ins', { ...q }),
}

export interface SaveRetentionPolicy {
  name: string
  description: string | null
  retentionDays: number
  action: RetentionAction
  appliesToAllMailboxes: boolean
  isEnabled: boolean
}

export const complianceApi = {
  retentionPolicies: () => api.get<RetentionPolicy[]>('/compliance/retention-policies'),
  createRetentionPolicy: (body: SaveRetentionPolicy) => api.post<RetentionPolicy>('/compliance/retention-policies', body),
  updateRetentionPolicy: (id: string, body: SaveRetentionPolicy) => api.put<RetentionPolicy>(`/compliance/retention-policies/${id}`, body),
  legalHolds: () => api.get<LegalHold[]>('/compliance/legal-holds'),
  createLegalHold: (body: { name: string; caseReference: string; description: string | null; mailboxIds: string[] }) =>
    api.post<LegalHold>('/compliance/legal-holds', body),
  releaseLegalHold: (id: string) => api.post<void>(`/compliance/legal-holds/${id}/release`),
  auditEvents: (q: PageQuery & { action?: string; privilegedOnly?: boolean }) => api.get<PagedResult<AuditEvent>>('/compliance/audit-events', { ...q }),
}
