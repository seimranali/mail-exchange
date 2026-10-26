import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { Navigate, useNavigate } from 'react-router-dom'
import { z } from 'zod'
import { Button } from '@/components/common/Button'
import { Icon } from '@/components/common/Icon'
import { FormField } from '@/components/forms/FormField'
import { EmailInput, PasswordInput } from '@/components/forms/TextInput'
import { zodResolver } from '@/lib/zodResolver'
import { errorMessage } from '@/services/apiClient'
import { webmailApi, webmailToken } from '@/services/webmail'
import styles from '../pages.module.css'

const schema = z.object({
  email: z.email('Enter your full email address.'),
  password: z.string().min(1, 'Enter your password.'),
})
type LoginForm = z.infer<typeof schema>

export default function WebmailLoginPage() {
  const navigate = useNavigate()
  const [formError, setFormError] = useState<string | null>(null)
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<LoginForm>({ resolver: zodResolver(schema) })

  useEffect(() => {
    document.title = 'Sign in · Mail'
  }, [])

  if (webmailToken.get()) return <Navigate to="/mail" replace />

  const onSubmit = handleSubmit(async ({ email, password }) => {
    setFormError(null)
    try {
      const res = await webmailApi.login(email, password)
      webmailToken.set(res.accessToken)
      navigate('/mail', { replace: true })
    } catch (e) {
      setFormError(errorMessage(e))
    }
  })

  return (
    <div className={styles.loginPage}>
      <aside className={styles.loginAside} aria-hidden="true">
        <div className={styles.loginBrand}>
          <img src="/mx-icon.svg" alt="" width={36} height={36} />
          Mail
        </div>
        <div className={styles.loginPitch}>
          <h2>Your inbox, anywhere.</h2>
          <ul>
            <li><Icon name="inbox" /> Read and organize your mail</li>
            <li><Icon name="send" /> Send with attachments, signed and encrypted in transit</li>
            <li><Icon name="shield" /> Spam and phishing filtered before it reaches you</li>
          </ul>
        </div>
        <p>Same account as your mail app</p>
      </aside>
      <main className={styles.loginMain}>
        <div className={styles.loginCard}>
          <h1>Sign in to Mail</h1>
          <p>Use your email address and mailbox password.</p>
          <form onSubmit={onSubmit} noValidate className={styles.formStack}>
            {formError && (
              <div className={styles.alertBanner} role="alert">
                <Icon name="error" />
                <span>{formError}</span>
              </div>
            )}
            <FormField label="Email" error={errors.email?.message} required>
              <EmailInput {...register('email')} autoFocus autoComplete="username" />
            </FormField>
            <FormField label="Password" error={errors.password?.message} required>
              <PasswordInput {...register('password')} autoComplete="current-password" />
            </FormField>
            <Button type="submit" variant="primary" size="lg" fullWidth loading={isSubmitting}>
              Sign in
            </Button>
          </form>
          <p className={styles.loginSwitch}>Administrator? <a href="/login">Sign in to MX Admin</a></p>
        </div>
      </main>
    </div>
  )
}
