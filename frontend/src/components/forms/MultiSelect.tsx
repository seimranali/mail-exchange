import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { cx } from '@/lib/cx'
import { Icon } from '../common/Icon'
import { useFieldContext } from './FormField'
import type { SelectOption } from './Select'
import styles from './forms.module.css'

interface MultiSelectProps {
  options: SelectOption[]
  value: string[]
  onChange: (value: string[]) => void
  placeholder?: string
  searchable?: boolean
  id?: string
  disabled?: boolean
}

/**
 * Multi-select following the WAI-ARIA combobox + multi-selectable listbox pattern.
 * Arrow keys move, Space/Enter toggles, Escape closes, typing filters.
 */
export function MultiSelect({ options, value, onChange, placeholder = 'Select…', searchable = true, id, disabled }: MultiSelectProps) {
  const field = useFieldContext()
  const inputId = id ?? field?.id
  const listId = useId()
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)

  const filtered = useMemo(
    () => options.filter((o) => o.label.toLowerCase().includes(query.trim().toLowerCase())),
    [options, query],
  )
  const selected = new Set(value)

  useEffect(() => {
    if (!open) return
    const onDocClick = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [open])

  useEffect(() => setActive(0), [query])

  const toggle = (v: string) => onChange(selected.has(v) ? value.filter((x) => x !== v) : [...value, v])

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      if (!open) setOpen(true)
      else setActive((a) => Math.min(a + 1, filtered.length - 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((a) => Math.max(a - 1, 0))
    } else if ((e.key === 'Enter' || (e.key === ' ' && !searchable)) && open) {
      e.preventDefault()
      const option = filtered[active]
      if (option && !option.disabled) toggle(option.value)
    } else if (e.key === 'Escape') {
      setOpen(false)
    } else if (e.key === 'Backspace' && !query && value.length) {
      onChange(value.slice(0, -1))
    }
  }

  const labelFor = (v: string) => options.find((o) => o.value === v)?.label ?? v

  return (
    <div ref={rootRef} className={styles.multiSelect}>
      <div
        className={cx(styles.inputWrap, styles.multiControl, field?.invalid && styles.invalid, disabled && styles.disabled)}
        onClick={() => !disabled && setOpen(true)}
      >
        {value.map((v) => (
          <span key={v} className={styles.chip}>
            {labelFor(v)}
            <button
              type="button"
              className={styles.chipRemove}
              aria-label={`Remove ${labelFor(v)}`}
              onClick={(e) => {
                e.stopPropagation()
                toggle(v)
              }}
              disabled={disabled}
            >
              <Icon name="close" size={12} />
            </button>
          </span>
        ))}
        <input
          id={inputId}
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={open && filtered[active] ? `${listId}-${active}` : undefined}
          aria-describedby={field?.describedBy}
          aria-invalid={field?.invalid || undefined}
          className={cx(styles.input, styles.multiInput)}
          placeholder={value.length ? '' : placeholder}
          value={query}
          readOnly={!searchable}
          disabled={disabled}
          onChange={(e) => {
            setQuery(e.target.value)
            setOpen(true)
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
        />
        <Icon name="chevronDown" size={16} className={styles.selectChevron} />
      </div>
      {open && (
        <ul id={listId} role="listbox" aria-multiselectable="true" className={styles.listbox}>
          {filtered.length === 0 && <li className={styles.listboxEmpty}>No matches</li>}
          {filtered.map((o, i) => (
            <li
              key={o.value}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={selected.has(o.value)}
              aria-disabled={o.disabled || undefined}
              className={cx(styles.option, i === active && styles.optionActive)}
              onMouseEnter={() => setActive(i)}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => !o.disabled && toggle(o.value)}
            >
              <span className={cx(styles.optionCheck, selected.has(o.value) && styles.optionChecked)} aria-hidden="true">
                {selected.has(o.value) && <Icon name="check" size={12} />}
              </span>
              {o.label}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
