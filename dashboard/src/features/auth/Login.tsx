import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { api, ApiError, errorMessage } from '../../api/client'
import type { AuthTokenInfo, Me } from '../../api/types'
import { Logo } from '../../components/icons'
import { FormMessage } from '../../components/ui'

const tooMany = (err: unknown) => (err instanceof ApiError && err.status === 429 ? 'Too many attempts, try again in a minute' : errorMessage(err))

function AuthCard({ title, subtitle, children, testId, onSubmit }: { title: string; subtitle: string; children: ReactNode; testId: string; onSubmit: (e: FormEvent) => void }) {
  return (
    <div className="login-page">
      <div className="login-glow" aria-hidden />
      <form className="login" onSubmit={onSubmit} data-testid={testId}>
        <div className="login-head">
          <Logo size={40} />
          <h1>{title}</h1>
          <p>{subtitle}</p>
        </div>
        {children}
      </form>
      <p className="login-foot">Self-hosted · data stays in your region</p>
    </div>
  )
}

export function Login({ onLogin }: { onLogin: (me: Me) => void }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [forgot, setForgot] = useState(false)
  const [sent, setSent] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      if (forgot) {
        await api.forgotPassword(email)
        setSent(true)
      } else {
        onLogin(await api.login(email, password))
      }
    } catch (err) {
      setError(tooMany(err))
    } finally {
      setBusy(false)
    }
  }

  const switchMode = (next: boolean) => {
    setForgot(next)
    setSent(false)
    setError(undefined)
  }

  if (forgot) {
    return (
      <AuthCard title="Reset your password" subtitle="We will email you a link to choose a new one" testId="forgot-form" onSubmit={submit}>
        <label className="field">
          <span>Email</span>
          <input type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} name="email" placeholder="you@example.com" />
        </label>
        {error && <FormMessage error={error} />}
        {sent && <div data-testid="forgot-sent"><FormMessage success="If an account exists for this email, a reset link is on its way. It expires in one hour." /></div>}
        <button type="submit" className="primary block" disabled={busy}>{busy ? 'Sending…' : 'Send reset link'}</button>
        <button type="button" className="link" onClick={() => switchMode(false)}>Back to sign in</button>
      </AuthCard>
    )
  }

  return (
    <AuthCard title="Welcome back" subtitle="Sign in to ReapDeep product analytics" testId="login-form" onSubmit={submit}>
      <label className="field">
        <span>Email</span>
        <input type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} name="email" placeholder="you@example.com" />
      </label>
      <label className="field">
        <span>Password</span>
        <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} name="password" placeholder="••••••••" />
      </label>
      {error && <div data-testid="login-error"><FormMessage error={error} /></div>}
      <button type="submit" className="primary block" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      <button type="button" className="link" onClick={() => switchMode(true)} data-testid="forgot-link">Forgot your password?</button>
    </AuthCard>
  )
}

export function SetPassword({ token, onDone }: { token: string; onDone: (me: Me) => void }) {
  const [info, setInfo] = useState<AuthTokenInfo | null>()
  const [invalid, setInvalid] = useState<string>()
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.authToken(token).then(setInfo, (err) => {
      setInfo(null)
      setInvalid(tooMany(err))
    })
  }, [token])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (password !== confirm) {
      setError('The passwords do not match')
      return
    }
    setBusy(true)
    setError(undefined)
    try {
      onDone(await api.resetPassword(token, password))
    } catch (err) {
      setError(tooMany(err))
    } finally {
      setBusy(false)
    }
  }

  const invite = info?.purpose === 'invite'
  return (
    <AuthCard title={invite ? 'Join ReapDeep' : 'Choose a new password'}
      subtitle={info ? `${invite ? 'Set a password for' : 'For'} ${info.email}` : info === null ? 'This link cannot be used' : 'Checking the link…'}
      testId="set-password-form" onSubmit={submit}>
      {info === null ? (
        <>
          <div data-testid="set-password-invalid"><FormMessage error={invalid} /></div>
          <a className="link" href="#/">Back to sign in</a>
        </>
      ) : (
        <>
          <label className="field">
            <span>New password</span>
            <input type="password" autoComplete="new-password" required minLength={8} value={password} onChange={(e) => setPassword(e.target.value)} name="password" placeholder="At least 8 characters" />
          </label>
          <label className="field">
            <span>Repeat password</span>
            <input type="password" autoComplete="new-password" required minLength={8} value={confirm} onChange={(e) => setConfirm(e.target.value)} name="confirm" />
          </label>
          {error && <div data-testid="set-password-error"><FormMessage error={error} /></div>}
          <button type="submit" className="primary block" disabled={busy || !info}>{busy ? 'Saving…' : invite ? 'Set password and sign in' : 'Change password and sign in'}</button>
        </>
      )}
    </AuthCard>
  )
}
