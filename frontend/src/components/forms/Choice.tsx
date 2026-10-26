import { forwardRef, useId, type InputHTMLAttributes, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { ValidationMessage } from './ValidationMessage'
import styles from './forms.module.css'

interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  label: ReactNode
  description?: ReactNode
}

export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox({ label, description, className, id, ...rest }, ref) {
  const generated = useId()
  const inputId = id ?? generated
  const descId = description ? `${inputId}-desc` : undefined
  return (
    <div className={cx(styles.choice, className)}>
      <input ref={ref} id={inputId} type="checkbox" className={styles.checkbox} aria-describedby={descId} {...rest} />
      <div>
        <label htmlFor={inputId} className={styles.choiceLabel}>{label}</label>
        {description && <p id={descId} className={styles.hint}>{description}</p>}
      </div>
    </div>
  )
})

interface RadioGroupProps<V extends string> {
  legend: ReactNode
  name: string
  value: V
  onChange: (value: V) => void
  options: { value: V; label: ReactNode; description?: ReactNode; disabled?: boolean }[]
  error?: string
  inline?: boolean
}

/** Native radios in a fieldset: arrow-key navigation and grouping come from the platform. */
export function RadioGroup<V extends string>({ legend, name, value, onChange, options, error, inline }: RadioGroupProps<V>) {
  const groupId = useId()
  return (
    <fieldset className={styles.fieldset} aria-describedby={error ? `${groupId}-error` : undefined}>
      <legend className={styles.label}>{legend}</legend>
      <div className={cx(styles.radioList, inline && styles.radioInline)}>
        {options.map((o) => {
          const optionId = `${groupId}-${o.value}`
          return (
            <div key={o.value} className={styles.choice}>
              <input
                id={optionId}
                type="radio"
                name={name}
                value={o.value}
                checked={value === o.value}
                disabled={o.disabled}
                onChange={() => onChange(o.value)}
                className={styles.radio}
                aria-describedby={o.description ? `${optionId}-desc` : undefined}
              />
              <div>
                <label htmlFor={optionId} className={styles.choiceLabel}>{o.label}</label>
                {o.description && <p id={`${optionId}-desc`} className={styles.hint}>{o.description}</p>}
              </div>
            </div>
          )
        })}
      </div>
      {error && <ValidationMessage id={`${groupId}-error`}>{error}</ValidationMessage>}
    </fieldset>
  )
}

interface ToggleSwitchProps {
  checked: boolean
  onChange: (checked: boolean) => void
  label: ReactNode
  description?: ReactNode
  disabled?: boolean
  id?: string
}

/** Switch pattern: a button with role="switch" and aria-checked. */
export function ToggleSwitch({ checked, onChange, label, description, disabled, id }: ToggleSwitchProps) {
  const generated = useId()
  const switchId = id ?? generated
  return (
    <div className={styles.toggleRow}>
      <div className={styles.toggleText}>
        <label htmlFor={switchId} className={styles.choiceLabel}>{label}</label>
        {description && <p id={`${switchId}-desc`} className={styles.hint}>{description}</p>}
      </div>
      <button
        id={switchId}
        type="button"
        role="switch"
        aria-checked={checked}
        aria-describedby={description ? `${switchId}-desc` : undefined}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={cx(styles.switch, checked && styles.switchOn)}
      >
        <span className={styles.switchThumb} />
      </button>
    </div>
  )
}
