import { formatBytes, formatDateTime, formatRelative, humanize } from '@/lib/format'
import type { Mailbox } from '@/types/api'
import { Card } from '../common/Card'
import { ProgressBar } from '../data-display/Metrics'
import { StatusBadge } from '../data-display/StatusBadge'
import styles from './mail.module.css'

export function MailboxCard({ mailbox }: { mailbox: Mailbox }) {
  return (
    <Card as="article" title={mailbox.displayName} description={mailbox.emailAddress} actions={<StatusBadge status={mailbox.status} />}>
      <ProgressBar
        label="Storage used"
        value={mailbox.usedBytes}
        max={mailbox.quotaBytes}
        showValue
        valueText={`${formatBytes(mailbox.usedBytes)} of ${formatBytes(mailbox.quotaBytes)}`}
      />
      <dl className={styles.facts}>
        <div>
          <dt>Type</dt>
          <dd>{humanize(mailbox.mailboxType)}</dd>
        </div>
        <div>
          <dt>Last sign-in</dt>
          <dd title={formatDateTime(mailbox.lastLoginAt)}>{formatRelative(mailbox.lastLoginAt)}</dd>
        </div>
        <div>
          <dt>Created</dt>
          <dd>{formatDateTime(mailbox.createdAt)}</dd>
        </div>
        <div>
          <dt>Aliases</dt>
          <dd>{mailbox.aliases.length ? mailbox.aliases.join(', ') : 'None'}</dd>
        </div>
      </dl>
    </Card>
  )
}
