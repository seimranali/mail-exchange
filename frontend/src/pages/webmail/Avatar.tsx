import { cx } from '@/lib/cx'
import type { WebmailAddress } from '@/services/webmail'
import { initials, toneFor } from './mailUtils'
import styles from './webmail.module.css'

export function Avatar({ address, large }: { address: WebmailAddress | null | undefined; large?: boolean }) {
  return (
    <span className={cx(styles.avatar, large && styles.avatarLarge, styles[`tone${toneFor(address?.address)}`])} aria-hidden="true">
      {initials(address)}
    </span>
  )
}
