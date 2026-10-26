import { formatBytes, formatDateTime, formatRelative } from '@/lib/format'
import type { AuthCheckResult, MessageSummary } from '@/types/api'
import { Icon } from '../common/Icon'
import { StatusBadge, type Tone } from '../data-display/StatusBadge'
import styles from './mail.module.css'

const authTone: Record<AuthCheckResult, Tone> = {
  Pass: 'success', Fail: 'danger', SoftFail: 'warning', Neutral: 'neutral', None: 'neutral', TempError: 'warning', PermError: 'danger',
}

function AuthBadge({ label, result }: { label: string; result: AuthCheckResult }) {
  return <StatusBadge status={result} tone={authTone[result]} label={`${label} ${result.toLowerCase()}`} dot={false} />
}

/** Received-message metadata with SPF / DKIM / DMARC results, for delivery troubleshooting. */
export function MessageList({ messages }: { messages: MessageSummary[] }) {
  return (
    <ul className={styles.messageList} aria-label="Recent messages">
      {messages.map((m) => (
        <li key={m.id} className={styles.messageItem}>
          <div className={styles.messageTop}>
            <span className={styles.messageSubject}>{m.subject || '(no subject)'}</span>
            <time className={styles.messageTime} dateTime={m.receivedAt} title={formatDateTime(m.receivedAt)}>{formatRelative(m.receivedAt)}</time>
          </div>
          <div className={styles.messageMeta}>
            {m.fromHeader ?? (m.envelopeFrom || '<> (bounce)')}
            {' · '}{formatBytes(m.sizeBytes)}
            {m.remoteIp && <> · from {m.remoteIp}</>}
            {m.receivedOverTls && <span className={styles.tlsTag}><Icon name="lock" size={12} /> TLS</span>}
          </div>
          <div className={styles.messageBadges}>
            {m.folder !== 'Inbox' && <StatusBadge status={m.folder} tone="warning" />}
            {/* SPF/DKIM/DMARC are checked on mail arriving from other servers; sent copies and internal mail have nothing to check. */}
            {m.folder !== 'Sent' && m.remoteIp && m.remoteIp !== '127.0.0.1' && (
              <>
                <AuthBadge label="SPF" result={m.spfResult} />
                <AuthBadge label="DKIM" result={m.dkimResult} />
                <AuthBadge label="DMARC" result={m.dmarcResult} />
              </>
            )}
            {m.spamScore !== null && (
              <StatusBadge status="score" dot={false} label={`Spam score ${m.spamScore.toFixed(1)}`}
                tone={m.spamScore >= 5 ? 'warning' : 'neutral'} />
            )}
          </div>
        </li>
      ))}
    </ul>
  )
}
