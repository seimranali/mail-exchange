import { forwardRef, useState, type InputHTMLAttributes, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { Icon, type IconName } from '../common/Icon'
import { useFieldContext } from './FormField'
import styles from './forms.module.css'

export interface TextInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'size' | 'prefix'> {
  icon?: IconName
  suffix?: ReactNode
  invalid?: boolean
  inputSize?: 'sm' | 'md'
}

/** Base text input. Picks up id and ARIA wiring from an enclosing FormField. */
export const TextInput = forwardRef<HTMLInputElement, TextInputProps>(function TextInput(
  { icon, suffix, invalid, inputSize = 'md', className, id, type = 'text', 'aria-describedby': describedBy, ...rest },
  ref,
) {
  const field = useFieldContext()
  const isInvalid = invalid ?? field?.invalid ?? false
  return (
    <div className={cx(styles.inputWrap, inputSize === 'sm' && styles.inputSm, isInvalid && styles.invalid, rest.disabled && styles.disabled, className)}>
      {icon && <Icon name={icon} size={16} className={styles.inputIcon} />}
      <input
        ref={ref}
        id={id ?? field?.id}
        type={type}
        className={styles.input}
        aria-invalid={isInvalid || undefined}
        aria-required={field?.required || rest.required || undefined}
        {...rest}
        aria-describedby={[field?.describedBy, describedBy].filter(Boolean).join(' ') || undefined}
      />
      {suffix && <span className={styles.inputSuffix}>{suffix}</span>}
    </div>
  )
})

export const EmailInput = forwardRef<HTMLInputElement, Omit<TextInputProps, 'type'>>(function EmailInput(props, ref) {
  return <TextInput ref={ref} type="email" autoComplete="email" spellCheck={false} inputMode="email" {...props} />
})

export const NumberInput = forwardRef<HTMLInputElement, Omit<TextInputProps, 'type'>>(function NumberInput(props, ref) {
  return <TextInput ref={ref} type="number" inputMode="numeric" {...props} />
})

export const PasswordInput = forwardRef<HTMLInputElement, Omit<TextInputProps, 'type' | 'suffix'>>(function PasswordInput(props, ref) {
  const [visible, setVisible] = useState(false)
  return (
    <TextInput
      ref={ref}
      type={visible ? 'text' : 'password'}
      autoComplete="current-password"
      spellCheck={false}
      {...props}
      suffix={
        <button
          type="button"
          className={styles.inputAction}
          onClick={() => setVisible((v) => !v)}
          aria-pressed={visible}
          aria-label={visible ? 'Hide password' : 'Show password'}
        >
          {visible ? 'Hide' : 'Show'}
        </button>
      }
    />
  )
})

interface SearchInputProps extends Omit<TextInputProps, 'type' | 'icon' | 'onChange' | 'value'> {
  value: string
  onValueChange: (value: string) => void
}

export const SearchInput = forwardRef<HTMLInputElement, SearchInputProps>(function SearchInput(
  { value, onValueChange, placeholder = 'Search', ...rest },
  ref,
) {
  return (
    <TextInput
      ref={ref}
      type="search"
      icon="search"
      value={value}
      placeholder={placeholder}
      aria-label={rest['aria-label'] ?? placeholder}
      onChange={(e) => onValueChange(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Escape' && value) {
          e.preventDefault()
          onValueChange('')
        }
      }}
      suffix={
        value ? (
          <button type="button" className={styles.inputAction} onClick={() => onValueChange('')} aria-label="Clear search">
            <Icon name="close" size={14} />
          </button>
        ) : undefined
      }
      {...rest}
    />
  )
})
