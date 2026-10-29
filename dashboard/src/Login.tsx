import { useState, type FormEvent } from 'react'
import { api, ApiError, type Me } from './api'

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
      setError(err instanceof ApiError && err.status === 429 ? 'Too many attempts, try again in a minute' : err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-page">
      <form className="card login" onSubmit={submit} data-testid="login-form">
        <div className="brand">ReapDeep</div>
        <label className="field">
          <span>Email</span>
          <input type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} name="email" />
        </label>
        <label className="field">
          <span>Password</span>
          <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} name="password" />
        </label>
        {error && <div className="login-error" role="alert" data-testid="login-error">{error}</div>}
        <button type="submit" className="primary" disabled={busy}>{busy ? 'Signing in...' : 'Sign in'}</button>
      </form>
    </div>
  )
}
