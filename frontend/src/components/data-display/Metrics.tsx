import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { cx } from '@/lib/cx'
import { Icon, type IconName } from '../common/Icon'
import type { Tone } from './StatusBadge'
import styles from './display.module.css'

interface MetricCardProps {
  label: string
  value: ReactNode
  icon?: IconName
  detail?: ReactNode
  tone?: Tone
  to?: string
}

export function MetricCard({ label, value, icon, detail, tone = 'info', to }: MetricCardProps) {
  const content = (
    <>
      <div className={styles.metricTop}>
        <span className={styles.metricLabel}>{label}</span>
        {icon && <span className={cx(styles.metricIcon, styles[tone])}><Icon name={icon} size={18} /></span>}
      </div>
      <div className={styles.metricValue}>{value}</div>
      {detail && <div className={styles.metricDetail}>{detail}</div>}
    </>
  )
  return to ? (
    <Link to={to} className={cx(styles.metric, styles.metricLink)}>{content}</Link>
  ) : (
    <div className={styles.metric}>{content}</div>
  )
}

interface ProgressBarProps {
  value: number
  max?: number
  label: string
  showValue?: boolean
  /** Switch to warning / danger tone above these percentages. */
  thresholds?: { warning: number; danger: number }
  valueText?: string
}

export function ProgressBar({ value, max = 100, label, showValue, thresholds = { warning: 75, danger: 90 }, valueText }: ProgressBarProps) {
  const pct = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0
  const tone = pct >= thresholds.danger ? 'danger' : pct >= thresholds.warning ? 'warning' : 'info'
  return (
    <div className={styles.progress}>
      {showValue && (
        <div className={styles.progressLabelRow}>
          <span>{label}</span>
          <span className={styles.progressValue}>{valueText ?? `${Math.round(pct)}%`}</span>
        </div>
      )}
      <div
        className={styles.progressTrack}
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={max}
        aria-valuenow={Math.round(value)}
        aria-valuetext={valueText ?? `${Math.round(pct)}%`}
      >
        <div className={cx(styles.progressFill, styles[tone])} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

export interface TimelineItem {
  id: string
  title: ReactNode
  time: string
  description?: ReactNode
  tone?: Tone
}

export function Timeline({ items, label }: { items: TimelineItem[]; label: string }) {
  return (
    <ol className={styles.timeline} aria-label={label}>
      {items.map((item) => (
        <li key={item.id} className={styles.timelineItem}>
          <span className={cx(styles.timelineDot, styles[item.tone ?? 'info'])} aria-hidden="true" />
          <div className={styles.timelineContent}>
            <div className={styles.timelineTitle}>{item.title}</div>
            {item.description && <div className={styles.timelineDescription}>{item.description}</div>}
            <div className={styles.timelineTime}>{item.time}</div>
          </div>
        </li>
      ))}
    </ol>
  )
}

export interface ActivityItem {
  id: string
  icon: IconName
  text: ReactNode
  time: string
  tone?: Tone
}

export function ActivityFeed({ items, label }: { items: ActivityItem[]; label: string }) {
  return (
    <ul className={styles.feed} aria-label={label}>
      {items.map((item) => (
        <li key={item.id} className={styles.feedItem}>
          <span className={cx(styles.feedIcon, styles[item.tone ?? 'neutral'])}><Icon name={item.icon} size={15} /></span>
          <div className={styles.feedText}>{item.text}</div>
          <time className={styles.feedTime}>{item.time}</time>
        </li>
      ))}
    </ul>
  )
}
