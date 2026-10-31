import { useState, type FormEvent } from 'react'
import { api, ApiError, errorMessage } from '../../api/client'
import type { Me } from '../../api/types'
import { Logo } from '../../components/icons'
import { FormMessage } from '../../components/ui'

export function Login({ onLogin }: { onLogin: (me: Me) => void }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      onLogin(await api.login(email, password))
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? 'Too many attempts, try again in a minute' : errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-page">
      <div className="login-glow" aria-hidden />
      <form className="login" onSubmit={submit} data-testid="login-form">
        <div className="login-head">
          <Logo size={40} />
          <h1>Welcome back</h1>
          <p>Sign in to ReapDeep product analytics</p>
        </div>
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
      </form>
      <p className="login-foot">Self-hosted · data stays in your region</p>
    </div>
  )
}
