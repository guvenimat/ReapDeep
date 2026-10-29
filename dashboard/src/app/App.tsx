import { useEffect, useState } from 'react'
import { api, setUnauthorizedHandler } from '../api/client'
import type { Me } from '../api/types'
import { Logo } from '../components/icons'
import { Login } from '../features/auth/Login'
import { Workspace } from './Workspace'

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)

  useEffect(() => {
    setUnauthorizedHandler(() => setMe(null))
    api.me().then(setMe, () => setMe(null))
  }, [])

  if (me === undefined) return <div className="splash"><Logo size={36} /></div>
  if (me === null) return <Login onLogin={setMe} />
  return <Workspace me={me} onLogout={() => api.logout().finally(() => setMe(null))}
    onAccessChanged={() => api.me().then(setMe, () => setMe(null))} />
}

