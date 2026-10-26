import { cloneElement, useEffect, useId, useState, type ReactElement } from 'react'
import { cx } from '@/lib/cx'
import styles from './overlays.module.css'

interface TooltipProps {
  content: string
  children: ReactElement<{ 'aria-describedby'?: string }>
  placement?: 'top' | 'bottom'
}

/**
 * Supplementary description shown on hover and keyboard focus. Hoverable and dismissible
 * with Escape (WCAG 1.4.13). Never put essential information only in a tooltip.
 */
export function Tooltip({ content, children, placement = 'top' }: TooltipProps) {
  const id = useId()
  const [open, setOpen] = useState(false)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open])

  return (
    <span
      className={styles.tooltipRoot}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)}
      onBlur={() => setOpen(false)}
    >
      {cloneElement(children, { 'aria-describedby': id })}
      <span id={id} role="tooltip" className={cx(styles.tooltip, styles[placement], open && styles.tooltipOpen)}>
        {content}
      </span>
    </span>
  )
}
