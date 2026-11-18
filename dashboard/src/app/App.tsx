import { useEffect, useState } from 'react'
import { api, setUnauthorizedHandler } from '../api/client'
import type { Me } from '../api/types'
import { Logo } from '../components/icons'
import { Login, SetPassword } from '../features/auth/Login'
import { Workspace } from './Workspace'

function linkToken(): string | undefined {
  const [path, query = ''] = location.hash.replace(/^#\/?/, '').split('?')
  return path === 'invite' || path === 'reset' ? new URLSearchParams(query).get('token') ?? '' : undefined
}

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)
  const [token, setToken] = useState(linkToken)

  useEffect(() => {
    setUnauthorizedHandler(() => setMe(null))
    api.me().then(setMe, () => setMe(null))
    const onHash = () => setToken(linkToken())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  if (token !== undefined) {
    return <SetPassword key={token} token={token} onDone={(next) => {
      history.replaceState(null, '', '#/')
      setToken(undefined)
      setMe(next)
    }} />
  }
  if (me === undefined) return <div className="splash"><Logo size={36} /></div>
  if (me === null) return <Login onLogin={setMe} />
  return <Workspace me={me} onLogout={() => api.logout().finally(() => setMe(null))}
    onAccessChanged={() => api.me().then(setMe, () => setMe(null))} />
}
