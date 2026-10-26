import { createContext, useContext, useId, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { ValidationMessage } from './ValidationMessage'
import styles from './forms.module.css'

interface FieldContextValue {
  id: string
  describedBy?: string
  invalid: boolean
  required: boolean
}

const FieldContext = createContext<FieldContextValue | null>(null)

/** Lets inputs pick up id / aria-describedby / aria-invalid from the surrounding FormField. */
export function useFieldContext() {
  return useContext(FieldContext)
}

interface FormFieldProps {
  label: ReactNode
  hint?: ReactNode
  error?: string
  required?: boolean
  /** Visually hide the label while keeping it accessible. */
  hideLabel?: boolean
  id?: string
  className?: string
  children: ReactNode
}

export function FormField({ label, hint, error, required = false, hideLabel, id, className, children }: FormFieldProps) {
  const generated = useId()
  const fieldId = id ?? `field-${generated}`
  const hintId = hint ? `${fieldId}-hint` : undefined
  const errorId = error ? `${fieldId}-error` : undefined
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined

  return (
    <FieldContext.Provider value={{ id: fieldId, describedBy, invalid: Boolean(error), required }}>
      <div className={cx(styles.field, className)}>
        <label htmlFor={fieldId} className={cx(styles.label, hideLabel && 'mx-visually-hidden')}>
          {label}
          {required && <span className={styles.required} aria-hidden="true"> *</span>}
        </label>
        {children}
        {hint && <p id={hintId} className={styles.hint}>{hint}</p>}
        {error && <ValidationMessage id={errorId}>{error}</ValidationMessage>}
      </div>
    </FieldContext.Provider>
  )
}
