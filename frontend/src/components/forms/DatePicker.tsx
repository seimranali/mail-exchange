import { forwardRef } from 'react'
import { TextInput, type TextInputProps } from './TextInput'

interface DatePickerProps extends Omit<TextInputProps, 'type' | 'icon'> {
  withTime?: boolean
}

/**
 * Date picker built on the native date input: every browser ships an accessible,
 * localized calendar with full keyboard support, which a custom grid would have to replicate.
 */
export const DatePicker = forwardRef<HTMLInputElement, DatePickerProps>(function DatePicker({ withTime, ...rest }, ref) {
  return <TextInput ref={ref} type={withTime ? 'datetime-local' : 'date'} icon="calendar" {...rest} />
})
