import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { z } from 'zod'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { Icon } from '@/components/common/Icon'
import { EmailInput, PasswordInput } from '@/components/forms/TextInput'
import { FormField } from '@/components/forms/FormField'
import { errorMessage } from '@/services/apiClient'
import { zodResolver } from '@/lib/zodResolver'
import styles from './pages.module.css'

const schema = z.object({
  email: z.email('Enter a valid email address.'),
  password: z.string().min(1, 'Enter your password.'),
})
type LoginForm = z.infer<typeof schema>

export function LoginPage() {
  const { status, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [formError, setFormError] = useState<string | null>(null)
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<LoginForm>({ resolver: zodResolver(schema) })

  useEffect(() => {
    document.title = 'Sign in · MX Admin'
  }, [])

  if (status === 'authenticated') return <Navigate to="/" replace />

  const onSubmit = handleSubmit(async ({ email, password }) => {
    setFormError(null)
    try {
      await login(email, password)
      navigate((location.state as { from?: string } | null)?.from ?? '/', { replace: true })
    } catch (e) {
      setFormError(errorMessage(e))
    }
  })

  return (
    <div className={styles.loginPage}>
      <aside className={styles.loginAside} aria-hidden="true">
        <div className={styles.loginBrand}>
          <img src="/mx-icon.svg" alt="" width={36} height={36} />
          MX Admin
        </div>
        <div className={styles.loginPitch}>
          <h2>Mail infrastructure, under control.</h2>
          <ul>
            <li><Icon name="mailbox" /> Mailboxes, domains and routing</li>
            <li><Icon name="shield" /> Threat protection and quarantine</li>
            <li><Icon name="compliance" /> Retention, legal hold and audit</li>
          </ul>
        </div>
        <p>Multi-tenant mail administration</p>
      </aside>
      <main className={styles.loginMain}>
        <div className={styles.loginCard}>
          <h1>Sign in</h1>
          <p>Use your administrator account.</p>
          <form onSubmit={onSubmit} noValidate className={styles.formStack}>
            {formError && (
              <div className={styles.alertBanner} role="alert">
                <Icon name="error" />
                <span>{formError}</span>
              </div>
            )}
            <FormField label="Email" error={errors.email?.message} required>
              <EmailInput {...register('email')} autoFocus />
            </FormField>
            <FormField label="Password" error={errors.password?.message} required>
              <PasswordInput {...register('password')} />
            </FormField>
            <Button type="submit" variant="primary" size="lg" fullWidth loading={isSubmitting}>
              Sign in
            </Button>
          </form>
          <p className={styles.loginSwitch}>Want to read your email? <a href="/mail/login">Sign in to webmail</a></p>
        </div>
      </main>
    </div>
  )
}
