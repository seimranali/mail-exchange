// Mirrors the DTOs exposed by MX.Api. Enums are serialized as strings.

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface ProblemDetails {
  status: number
  title: string
  detail?: string
  errors?: Record<string, string[]>
  traceId?: string
}

export type Role = 'PlatformAdmin' | 'OrgAdmin' | 'MailAdmin' | 'SecurityAdmin' | 'ComplianceOfficer' | 'Auditor'

export interface CurrentUser {
  id: string
  email: string
  displayName: string
  organizationId: string
  organizationName: string
  roles: Role[]
}

export interface LoginResponse {
  accessToken: string
  expiresAt: string
  user: CurrentUser
}

export type OrganizationStatus = 'Active' | 'Suspended' | 'Deprovisioned'
export interface Organization {
  id: string
  name: string
  slug: string
  status: OrganizationStatus
  domainCount: number
  mailboxCount: number
  createdAt: string
}

export type DomainVerificationStatus = 'Pending' | 'Verified' | 'Failed'
export interface Domain {
  id: string
  domainName: string
  isDefault: boolean
  verificationStatus: DomainVerificationStatus
  verificationToken: string
  mxConfigured: boolean
  spfConfigured: boolean
  dkimConfigured: boolean
  dmarcConfigured: boolean
  verifiedAt: string | null
  certificateExpiresAt: string | null
  mailboxCount: number
  dkimSelector: string | null
  dkimPublicKey: string | null
}

/** TLS certificate the mail server serves on SMTP/IMAP (one per server host name, covers all domains). */
export interface MailCertificate {
  issued: boolean
  hostname: string | null
  dnsNames: string[]
  issuer: string | null
  notBefore: string | null
  notAfter: string | null
  renewsAt: string | null
  autoRenew: boolean
  ports: number[]
}

export interface DkimKey {
  selector: string
  keyBits: number
  dnsName: string
  dnsValue: string
  createdAt: string
}

export type MailboxType = 'User' | 'Shared' | 'Resource'
export type MailboxStatus = 'Active' | 'Disabled' | 'Provisioning' | 'Deleted'
export interface Mailbox {
  id: string
  domainId: string
  emailAddress: string
  displayName: string
  mailboxType: MailboxType
  status: MailboxStatus
  quotaBytes: number
  usedBytes: number
  lastLoginAt: string | null
  createdAt: string
  aliases: string[]
  hasClientPassword: boolean
}

export interface CreateMailboxRequest {
  domainId: string
  localPart: string
  displayName: string
  mailboxType: MailboxType
  quotaBytes: number
}

export interface UpdateMailboxRequest {
  displayName: string
  quotaBytes: number
  enabled: boolean
}

export type AlertSeverity = 'Low' | 'Medium' | 'High' | 'Critical'
export type AlertStatus = 'Open' | 'Acknowledged' | 'Resolved' | 'Dismissed'
export interface SecurityAlert {
  id: string
  title: string
  description: string
  category: string
  severity: AlertSeverity
  status: AlertStatus
  mailboxId: string | null
  detectedAt: string
  resolvedAt: string | null
}

export type QuarantineReason = 'Spam' | 'Malware' | 'Phishing' | 'Policy'
export type QuarantineStatus = 'Held' | 'Released' | 'Deleted'
export interface QuarantineRecord {
  id: string
  messageId: string
  sender: string
  recipient: string
  subject: string | null
  reason: QuarantineReason
  status: QuarantineStatus
  spamScore: number | null
  sizeBytes: number
  receivedAt: string
  expiresAt: string
}

export interface SecurityPolicy {
  passwordMinLength: number
  requireMfa: boolean
  sessionTimeoutMinutes: number
  maxFailedLogins: number
  lockoutMinutes: number
  enforceTls: boolean
  allowedIpRanges: string[]
}

export type AuthenticationOutcome = 'Success' | 'InvalidCredentials' | 'LockedOut' | 'MfaRequired' | 'Disabled'
export interface AuthenticationEvent {
  id: string
  attemptedEmail: string
  outcome: AuthenticationOutcome
  ipAddress: string | null
  occurredAt: string
}

export type RetentionAction = 'Retain' | 'Delete' | 'Archive'
export interface RetentionPolicy {
  id: string
  name: string
  description: string | null
  retentionDays: number
  action: RetentionAction
  appliesToAllMailboxes: boolean
  isEnabled: boolean
}

export type LegalHoldStatus = 'Active' | 'Released'
export interface LegalHold {
  id: string
  name: string
  caseReference: string
  description: string | null
  status: LegalHoldStatus
  mailboxCount: number
  startsAt: string
  releasedAt: string | null
}

export interface AuditEvent {
  id: string
  actorUserId: string | null
  action: string
  entityType: string
  entityId: string | null
  ipAddress: string | null
  isPrivileged: boolean
  occurredAt: string
}

export type MailServerStatus = 'Online' | 'Degraded' | 'Offline' | 'Maintenance'
export interface ServerStatus {
  id: string
  name: string
  hostname: string
  status: MailServerStatus
  cpuPercent: number | null
  memoryPercent: number | null
  diskPercent: number | null
  queueLength: number | null
  capturedAt: string | null
}

export interface DashboardSummary {
  activeMailboxes: number
  mailboxLimit: number
  storageUsedBytes: number
  storageLimitBytes: number
  domains: number
  verifiedDomains: number
  openAlerts: number
  criticalAlerts: number
  quarantineHeld: number
  servers: ServerStatus[]
  recentActivity: { id: string; action: string; entityType: string; occurredAt: string }[]
}

export type AuthCheckResult = 'None' | 'Pass' | 'Fail' | 'SoftFail' | 'Neutral' | 'TempError' | 'PermError'
export type MailFolder = 'Inbox' | 'Junk' | 'Sent' | 'Drafts' | 'Trash' | 'Archive'

/** Envelope and authentication facts only; administrators never see message content. */
export interface MessageSummary {
  id: string
  folder: MailFolder
  envelopeFrom: string
  fromHeader: string | null
  subject: string | null
  sizeBytes: number
  remoteIp: string | null
  receivedOverTls: boolean
  spfResult: AuthCheckResult
  dkimResult: AuthCheckResult
  dmarcResult: AuthCheckResult
  receivedAt: string
  spamScore: number | null
}

export interface QueuedMessage {
  queueId: string
  sender: string
  recipient: string
  sizeBytes: number
  state: string
  queuedAt: string
  lastError: string | null
  attempts: number
  nextAttemptAt: string | null
  remoteHost: string | null
  subject: string | null
  deliveredAt: string | null
}
