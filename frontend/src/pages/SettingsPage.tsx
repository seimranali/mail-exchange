import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Controller, useForm } from 'react-hook-form'
import { z } from 'zod'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { Card } from '@/components/common/Card'
import { ErrorState, LoadingState } from '@/components/data-display/States'
import { RadioGroup, ToggleSwitch } from '@/components/forms/Choice'
import { FormField } from '@/components/forms/FormField'
import { NumberInput, TextInput } from '@/components/forms/TextInput'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useTheme, type ThemePreference } from '@/hooks/useTheme'
import { ContentContainer, PageHeader, Stack } from '@/layouts/PageLayout'
import { zodResolver } from '@/lib/zodResolver'
import { securityApi } from '@/services/endpoints'
import styles from './pages.module.css'

export default function SettingsPage() {
  const { hasRole } = useAuth()
  const { theme, setTheme } = useTheme()
  const canSeePolicy = hasRole('PlatformAdmin', 'OrgAdmin', 'SecurityAdmin', 'Auditor')

  return (
    <ContentContainer narrow>
      <PageHeader title="Settings" description="Personal preferences and organization security policy." />
      <Stack gap="lg">
        <Card title="Appearance">
          <RadioGroup<ThemePreference>
            legend="Theme"
            name="theme"
            value={theme}
            onChange={setTheme}
            inline
            options={[
              { value: 'system', label: 'Match system' },
              { value: 'light', label: 'Light' },
              { value: 'dark', label: 'Dark' },
            ]}
          />
        </Card>
        {canSeePolicy && <SecurityPolicyCard readOnly={!hasRole('PlatformAdmin', 'OrgAdmin', 'SecurityAdmin')} />}
      </Stack>
    </ContentContainer>
  )
}

const schema = z.object({
  passwordMinLength: z.coerce.number<number>().int().min(8, 'At least 8.').max(128),
  requireMfa: z.boolean(),
  sessionTimeoutMinutes: z.coerce.number<number>().int().min(5, 'At least 5 minutes.').max(1440),
  maxFailedLogins: z.coerce.number<number>().int().min(1).max(50),
  lockoutMinutes: z.coerce.number<number>().int().min(1).max(1440),
  enforceTls: z.boolean(),
  allowedIpRanges: z.string().refine(
    (v) => v.split(/[\s,]+/).filter(Boolean).every((r) => /^[0-9a-f:.]+\/\d{1,3}$/i.test(r)),
    'Use CIDR notation, e.g. 203.0.113.0/24, separated by commas.',
  ),
})
type PolicyForm = z.infer<typeof schema>

function SecurityPolicyCard({ readOnly }: { readOnly: boolean }) {
  const policy = useQuery({ queryKey: ['security', 'policy'], queryFn: securityApi.policy })
  const { register, handleSubmit, control, reset, formState: { errors, isDirty } } = useForm<PolicyForm>({ resolver: zodResolver(schema) })

  useEffect(() => {
    if (policy.data) reset({ ...policy.data, allowedIpRanges: policy.data.allowedIpRanges.join(', ') })
  }, [policy.data, reset])

  const save = useApiMutation({
    mutationFn: (v: PolicyForm) => securityApi.updatePolicy({ ...v, allowedIpRanges: v.allowedIpRanges.split(/[\s,]+/).filter(Boolean) }),
    invalidate: [['security', 'policy']],
    successMessage: 'Security policy saved',
    errorTitle: 'Could not save policy',
  })

  return (
    <Card title="Security policy" description="Applies to every administrator in your organization.">
      {policy.isLoading && <LoadingState label="Loading policy" />}
      {policy.error && <ErrorState error={policy.error} onRetry={() => policy.refetch()} />}
      {policy.data && (
        <form noValidate onSubmit={handleSubmit((v) => save.mutate(v))}>
          <fieldset disabled={readOnly} className={styles.formStack} style={{ border: 0, padding: 0, minWidth: 0 }}>
            <div className={styles.formGrid}>
              <FormField label="Minimum password length" error={errors.passwordMinLength?.message}><NumberInput {...register('passwordMinLength')} /></FormField>
              <FormField label="Session timeout" error={errors.sessionTimeoutMinutes?.message}>
                <NumberInput {...register('sessionTimeoutMinutes')} suffix={<span className={styles.suffixText}>min</span>} />
              </FormField>
              <FormField label="Failed sign-ins before lockout" error={errors.maxFailedLogins?.message}><NumberInput {...register('maxFailedLogins')} /></FormField>
              <FormField label="Lockout duration" error={errors.lockoutMinutes?.message}>
                <NumberInput {...register('lockoutMinutes')} suffix={<span className={styles.suffixText}>min</span>} />
              </FormField>
            </div>
            <FormField label="Allowed admin IP ranges" error={errors.allowedIpRanges?.message} hint="Leave empty to allow sign-in from any address.">
              <TextInput {...register('allowedIpRanges')} placeholder="203.0.113.0/24, 2001:db8::/32" spellCheck={false} />
            </FormField>
            <Controller control={control} name="requireMfa" render={({ field }) => (
              <ToggleSwitch label="Require multi-factor authentication" description="Administrators must enroll a second factor." checked={field.value} onChange={field.onChange} disabled={readOnly} />
            )} />
            <Controller control={control} name="enforceTls" render={({ field }) => (
              <ToggleSwitch label="Enforce TLS for mail transport" description="Reject SMTP sessions that cannot negotiate TLS." checked={field.value} onChange={field.onChange} disabled={readOnly} />
            )} />
            {!readOnly && (
              <div className={styles.formActions}>
                <Button type="submit" variant="primary" loading={save.isPending} disabled={!isDirty}>Save policy</Button>
              </div>
            )}
          </fieldset>
        </form>
      )}
    </Card>
  )
}
