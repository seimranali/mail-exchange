import { cx } from '@/lib/cx'
import styles from './common.module.css'

interface SpinnerProps {
  size?: 'sm' | 'md' | 'lg'
  label?: string
  className?: string
}

const sizeClass = { sm: styles.spinnerSm, md: styles.spinnerMd, lg: styles.spinnerLg }

export function Spinner({ size = 'md', label = 'Loading', className }: SpinnerProps) {
  return (
    <span role="status" className={cx(styles.spinner, sizeClass[size], className)}>
      <span className="mx-visually-hidden">{label}</span>
    </span>
  )
}
