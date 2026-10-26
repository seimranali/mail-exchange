import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { formatBytes, formatDateTime, formatRelative, humanize } from '@/lib/format'
import type { AuditEvent, QuarantineRecord, QueuedMessage, SecurityAlert, ServerStatus } from '@/types/api'
import { Button } from '../common/Button'
import { Icon } from '../common/Icon'
import { DataTable, type Column } from '../data-display/DataTable'
import { ProgressBar, Timeline, type TimelineItem } from '../data-display/Metrics'
import { StatusBadge } from '../data-display/StatusBadge'
import { DropdownMenu } from '../overlays/DropdownMenu'
import styles from './mail.module.css'

/* ---- ServerHealthCard ------------------------------------------------ */
export function ServerHealthCard({ server }: { server: ServerStatus }) {
  return (
    <article className={styles.serverCard} aria-label={`Server ${server.name}`}>
      <header className={styles.serverHeader}>
        <span className={styles.serverIcon}><Icon name="server" size={18} /></span>
        <div className={styles.serverTitle}>
          <h3>{server.name}</h3>
          <p>{server.hostname}</p>
        </div>
        <StatusBadge status={server.status} />
      </header>
      <div className={styles.serverMetrics}>
        <ProgressBar label="CPU" value={server.cpuPercent ?? 0} showValue />
        <ProgressBar label="Memory" value={server.memoryPercent ?? 0} showValue />
        <ProgressBar label="Disk" value={server.diskPercent ?? 0} showValue />
      </div>
      <footer className={styles.serverFooter}>
        <span>Queue: <strong>{server.queueLength ?? '—'}</strong></span>
        <span>Updated {formatRelative(server.capturedAt)}</span>
      </footer>
    </article>
  )
}

/* ---- SecurityAlertCard ----------------------------------------------- */
interface SecurityAlertCardProps {
  alert: SecurityAlert
  onAcknowledge?: () => void
  onResolve?: () => void
  onDismiss?: () => void
}

export function SecurityAlertCard({ alert, onAcknowledge, onResolve, onDismiss }: SecurityAlertCardProps) {
  const open = alert.status === 'Open' || alert.status === 'Acknowledged'
  return (
    <article className={cx(styles.alertCard, styles[`sev${alert.severity}`])} aria-labelledby={`alert-${alert.id}`}>
      <div className={styles.alertMain}>
        <div className={styles.alertBadges}>
          <StatusBadge status={alert.severity} />
          <span className={styles.alertCategory}>{alert.category}</span>
        </div>
        <h3 id={`alert-${alert.id}`} className={styles.alertTitle}>{alert.title}</h3>
        <p className={styles.alertDescription}>{alert.description}</p>
        <p className={styles.alertMeta}>
          Detected <time dateTime={alert.detectedAt} title={formatDateTime(alert.detectedAt)}>{formatRelative(alert.detectedAt)}</time>
          {' · '}
          <StatusBadge status={alert.status} dot={false} />
        </p>
      </div>
      {open && (onAcknowledge || onResolve) && (
        <div className={styles.alertActions}>
          {alert.status === 'Open' && onAcknowledge && <Button size="sm" onClick={onAcknowledge}>Acknowledge</Button>}
          {onResolve && <Button size="sm" variant="primary" onClick={onResolve}>Resolve</Button>}
          {onDismiss && (
            <DropdownMenu label="More actions" iconOnly icon="more" variant="ghost" items={[{ id: 'dismiss', label: 'Dismiss as false positive', onSelect: onDismiss }]} />
          )}
        </div>
      )}
    </article>
  )
}

/* ---- MailQueueTable -------------------------------------------------- */
export function MailQueueTable({ rows, loading, error, onRetry, onSelect }: {
  rows: QueuedMessage[] | undefined
  loading?: boolean
  error?: unknown
  onRetry?: () => void
  onSelect?: (m: QueuedMessage) => void
}) {
  const columns: Column<QueuedMessage>[] = [
    { id: 'id', header: 'Queue ID', cell: (m) => <code>{m.queueId}</code> },
    { id: 'sender', header: 'Sender', cell: (m) => m.sender, hideOnMobile: true },
    { id: 'recipient', header: 'Recipient', cell: (m) => m.recipient },
    { id: 'state', header: 'State', cell: (m) => <StatusBadge status={m.state} /> },
    { id: 'attempts', header: 'Attempts', cell: (m) => m.attempts ?? 0, align: 'end', hideOnMobile: true },
    {
      id: 'next', header: 'Next attempt', hideOnMobile: true,
      cell: (m) => (m.state === 'Deferred' || m.state === 'Queued') && m.nextAttemptAt ? formatRelative(m.nextAttemptAt) : '—',
    },
    { id: 'size', header: 'Size', cell: (m) => formatBytes(m.sizeBytes), align: 'end', hideOnMobile: true },
    { id: 'queued', header: 'Queued', cell: (m) => <span title={formatDateTime(m.queuedAt)}>{formatRelative(m.queuedAt)}</span>, align: 'end' },
  ]
  return (
    <DataTable caption="Mail queue" columns={columns} rows={rows} rowKey={(m) => m.queueId} loading={loading} error={error} onRetry={onRetry}
      onRowClick={onSelect} rowLabel={(m) => `Message ${m.queueId} to ${m.recipient}, ${m.state}`} />
  )
}

/* ---- MailFlowTimeline ------------------------------------------------ */
/** Delivery path of a single message, from its real queue state. */
export function MailFlowTimeline({ message }: { message: QueuedMessage }) {
  const attempts = message.attempts ?? 0
  const items: TimelineItem[] = [
    { id: 'queued', title: 'Queued for delivery', description: `From ${message.sender || '<> (bounce)'}`, time: formatDateTime(message.queuedAt), tone: 'success' },
  ]
  if (attempts > 0) {
    items.push({
      id: 'attempts',
      title: `${attempts} delivery attempt${attempts === 1 ? '' : 's'}`,
      description: message.remoteHost ? `Last server: ${message.remoteHost}` : undefined,
      time: '',
      tone: 'info',
    })
  }
  switch (message.state) {
    case 'Delivered':
      items.push({ id: 'done', title: 'Delivered', description: message.lastError ?? undefined, time: formatDateTime(message.deliveredAt), tone: 'success' })
      break
    case 'Deferred':
      items.push({ id: 'deferred', title: 'Deferred: will retry', description: message.lastError ?? 'Temporary failure', time: message.nextAttemptAt ? `Next attempt ${formatRelative(message.nextAttemptAt)}` : '', tone: 'warning' })
      break
    case 'Failed':
      items.push({ id: 'failed', title: 'Failed permanently', description: `${message.lastError ?? 'Rejected'}. A bounce was sent to the sender.`, time: '', tone: 'danger' })
      break
    case 'Cancelled':
      items.push({ id: 'cancelled', title: 'Cancelled', description: message.lastError ?? undefined, time: '', tone: 'neutral' })
      break
    default:
      items.push({ id: 'pending', title: message.state === 'Sending' ? 'Sending now' : 'Waiting for first attempt', time: '', tone: 'info' })
  }
  return <Timeline items={items} label={`Delivery timeline for ${message.recipient}`} />
}

/* ---- QuarantineTable ------------------------------------------------- */
export function QuarantineTable({ rows, loading, error, onRetry, onRelease, onDelete, footer, toolbar }: {
  rows: QuarantineRecord[] | undefined
  loading?: boolean
  error?: unknown
  onRetry?: () => void
  onRelease?: (r: QuarantineRecord) => void
  onDelete?: (r: QuarantineRecord) => void
  footer?: ReactNode
  toolbar?: ReactNode
}) {
  const columns: Column<QuarantineRecord>[] = [
    {
      id: 'message', header: 'Message', cell: (r) => (
        <div className={styles.cellStack}>
          <span className={styles.cellPrimary}>{r.subject ?? '(no subject)'}</span>
          <span className={styles.cellSecondary}>{r.sender} → {r.recipient}</span>
        </div>
      ),
    },
    { id: 'reason', header: 'Reason', cell: (r) => <StatusBadge status={r.reason} /> },
    { id: 'score', header: 'Score', cell: (r) => r.spamScore ?? '—', align: 'end', hideOnMobile: true },
    { id: 'status', header: 'Status', cell: (r) => <StatusBadge status={r.status} dot={false} />, hideOnMobile: true },
    { id: 'received', header: 'Received', cell: (r) => <span title={formatDateTime(r.receivedAt)}>{formatRelative(r.receivedAt)}</span>, hideOnMobile: true },
    {
      id: 'actions', header: 'Actions', align: 'end', cell: (r) => r.status === 'Held' && (onRelease || onDelete) ? (
        <DropdownMenu
          label={`Actions for message from ${r.sender}`}
          iconOnly
          icon="more"
          variant="ghost"
          items={[
            ...(onRelease ? [{ id: 'release', label: 'Release to inbox', icon: 'check' as const, onSelect: () => onRelease(r), disabled: r.reason === 'Malware' }] : []),
            ...(onDelete ? [{ id: 'delete', label: 'Delete permanently', icon: 'trash' as const, onSelect: () => onDelete(r), danger: true }] : []),
          ]}
        />
      ) : null,
    },
  ]
  return <DataTable caption="Quarantined messages" columns={columns} rows={rows} rowKey={(r) => r.id} loading={loading} error={error} onRetry={onRetry} footer={footer} toolbar={toolbar} />
}

/* ---- AuditEventTable ------------------------------------------------- */
export function AuditEventTable({ rows, loading, error, onRetry, footer, toolbar }: {
  rows: AuditEvent[] | undefined
  loading?: boolean
  error?: unknown
  onRetry?: () => void
  footer?: ReactNode
  toolbar?: ReactNode
}) {
  const columns: Column<AuditEvent>[] = [
    { id: 'time', header: 'Time', cell: (e) => formatDateTime(e.occurredAt), width: '190px' },
    {
      id: 'action', header: 'Action', cell: (e) => (
        <span className={styles.auditAction}>
          <code>{e.action}</code>
          {e.isPrivileged && <StatusBadge status="Privileged" tone="accent" dot={false} />}
        </span>
      ),
    },
    { id: 'entity', header: 'Entity', cell: (e) => humanize(e.entityType), hideOnMobile: true },
    { id: 'actor', header: 'Actor', cell: (e) => (e.actorUserId ? <code title={e.actorUserId}>{e.actorUserId.slice(0, 8)}</code> : 'System'), hideOnMobile: true },
    { id: 'ip', header: 'IP address', cell: (e) => e.ipAddress ?? '—', hideOnMobile: true },
  ]
  return <DataTable caption="Audit events" columns={columns} rows={rows} rowKey={(e) => e.id} loading={loading} error={error} onRetry={onRetry} footer={footer} toolbar={toolbar} />
}
