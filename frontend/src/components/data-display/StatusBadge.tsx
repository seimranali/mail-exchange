import { cx } from '@/lib/cx'
import { humanize } from '@/lib/format'
import styles from './display.module.css'

export type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'accent'

// Maps domain statuses to tones once, so every module colors states consistently.
const toneByStatus: Record<string, Tone> = {
  active: 'success', online: 'success', verified: 'success', succeeded: 'success', resolved: 'success', released: 'neutral', success: 'success',
  provisioning: 'info', pending: 'info', queued: 'info', running: 'info', acknowledged: 'info', trial: 'info',
  degraded: 'warning', maintenance: 'warning', medium: 'warning', deferred: 'warning', held: 'warning', spam: 'warning', pastdue: 'warning',
  disabled: 'neutral', dismissed: 'neutral', low: 'neutral', cancelled: 'neutral', deleted: 'neutral', policy: 'neutral',
  offline: 'danger', failed: 'danger', high: 'danger', critical: 'danger', malware: 'danger', phishing: 'danger', suspended: 'danger',
  lockedout: 'danger', invalidcredentials: 'danger',
}

interface StatusBadgeProps {
  status: string
  tone?: Tone
  label?: string
  dot?: boolean
}

export function StatusBadge({ status, tone, label, dot = true }: StatusBadgeProps) {
  const resolved = tone ?? toneByStatus[status.toLowerCase()] ?? 'neutral'
  return (
    <span className={cx(styles.badge, styles[resolved])}>
      {dot && <span className={styles.badgeDot} aria-hidden="true" />}
      {label ?? humanize(status)}
    </span>
  )
}
