import { forwardRef, type SelectHTMLAttributes } from 'react'
import { cx } from '@/lib/cx'
import { Icon } from '../common/Icon'
import { useFieldContext } from './FormField'
import styles from './forms.module.css'

export interface SelectOption<V extends string = string> {
  value: V
  label: string
  disabled?: boolean
}

interface SelectProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'size'> {
  options: SelectOption[]
  placeholder?: string
  invalid?: boolean
  inputSize?: 'sm' | 'md'
}

/** Styled native select: keeps full platform keyboard and screen reader support. */
export const Select = forwardRef<HTMLSelectElement, SelectProps>(function Select(
  { options, placeholder, invalid, inputSize = 'md', className, id, ...rest },
  ref,
) {
  const field = useFieldContext()
  const isInvalid = invalid ?? field?.invalid ?? false
  return (
    <div className={cx(styles.inputWrap, styles.selectWrap, inputSize === 'sm' && styles.inputSm, isInvalid && styles.invalid, rest.disabled && styles.disabled, className)}>
      <select
        ref={ref}
        id={id ?? field?.id}
        className={cx(styles.input, styles.select)}
        aria-invalid={isInvalid || undefined}
        aria-describedby={field?.describedBy}
        {...rest}
      >
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {options.map((o) => (
          <option key={o.value} value={o.value} disabled={o.disabled}>
            {o.label}
          </option>
        ))}
      </select>
      <Icon name="chevronDown" size={16} className={styles.selectChevron} />
    </div>
  )
})
