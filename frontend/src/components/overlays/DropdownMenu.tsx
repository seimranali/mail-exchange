import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { cx } from '@/lib/cx'
import { Button, IconButton, type ButtonVariant } from '../common/Button'
import { Icon, type IconName } from '../common/Icon'
import styles from './overlays.module.css'

export interface MenuItem {
  id: string
  label: string
  icon?: IconName
  onSelect: () => void
  danger?: boolean
  disabled?: boolean
  /** Renders as menuitemcheckbox. */
  checked?: boolean
  keepOpen?: boolean
}

interface DropdownMenuProps {
  label: string
  items: MenuItem[]
  icon?: IconName
  /** Icon-only trigger; `label` becomes its accessible name. */
  iconOnly?: boolean
  variant?: ButtonVariant
  align?: 'start' | 'end'
  size?: 'sm' | 'md'
}

/** WAI-ARIA menu button: arrows/Home/End move, Enter/Space select, Escape closes and returns focus. */
export function DropdownMenu({ label, items, icon = 'chevronDown', iconOnly, variant = 'default', align = 'end', size = 'sm' }: DropdownMenuProps) {
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const menuId = useId()
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLUListElement>(null)
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    menuRef.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]')[active]?.focus()
  }, [open, active])

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [open])

  const close = (focusTrigger = true) => {
    setOpen(false)
    if (focusTrigger) triggerRef.current?.focus()
  }

  const openAt = (index: number) => {
    setActive(index)
    setOpen(true)
  }

  const onTriggerKey = (e: KeyboardEvent) => {
    if (e.key === 'ArrowDown' || e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      openAt(0)
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      openAt(items.length - 1)
    }
  }

  const onMenuKey = (e: KeyboardEvent) => {
    const last = items.length - 1
    if (e.key === 'ArrowDown') setActive((a) => (a >= last ? 0 : a + 1))
    else if (e.key === 'ArrowUp') setActive((a) => (a <= 0 ? last : a - 1))
    else if (e.key === 'Home') setActive(0)
    else if (e.key === 'End') setActive(last)
    else if (e.key === 'Escape') close()
    else if (e.key === 'Tab') close(false)
    else return
    e.preventDefault()
  }

  const select = (item: MenuItem) => {
    if (item.disabled) return
    item.onSelect()
    if (!item.keepOpen) close()
  }

  const triggerProps = {
    ref: triggerRef,
    'aria-haspopup': 'menu' as const,
    'aria-expanded': open,
    'aria-controls': open ? menuId : undefined,
    onClick: () => (open ? close() : openAt(0)),
    onKeyDown: onTriggerKey,
  }

  return (
    <div ref={rootRef} className={styles.menuRoot}>
      {iconOnly ? (
        <IconButton icon={icon} label={label} variant={variant} size={size} {...triggerProps} />
      ) : (
        <Button variant={variant} size={size} icon={icon === 'chevronDown' ? undefined : icon} iconEnd="chevronDown" {...triggerProps}>
          {label}
        </Button>
      )}
      {open && (
        <ul id={menuId} ref={menuRef} role="menu" aria-label={label} className={cx(styles.menu, align === 'end' ? styles.alignEnd : styles.alignStart)} onKeyDown={onMenuKey}>
          {items.map((item, i) => (
            <li
              key={item.id}
              role={item.checked === undefined ? 'menuitem' : 'menuitemcheckbox'}
              aria-checked={item.checked}
              aria-disabled={item.disabled || undefined}
              tabIndex={i === active ? 0 : -1}
              className={cx(styles.menuItem, item.danger && styles.menuItemDanger)}
              onClick={() => select(item)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  select(item)
                }
              }}
              onMouseEnter={() => setActive(i)}
            >
              {item.checked !== undefined ? (
                <span className={cx(styles.menuCheck, item.checked && styles.menuChecked)} aria-hidden="true">
                  {item.checked && <Icon name="check" size={12} />}
                </span>
              ) : (
                item.icon && <Icon name={item.icon} size={16} />
              )}
              {item.label}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
