import { useEffect, useState } from 'react'
import { api, setProject, setUnauthorizedHandler, storedProjectId, type Me } from './api'
import { Events } from './Events'
import { Funnels } from './Funnels'
import { Dashboards } from './Insights'
import { Login } from './Login'
import { Persons } from './Persons'
import { Settings } from './Settings'
import { RANGE_PRESETS, rangeFromParams, TABS, useRoute, type RangePreset } from './route'
import { Trends } from './Trends'
import { MetaContext, MetaDatalists, useQuery } from './ui'

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)

  useEffect(() => {
    setUnauthorizedHandler(() => setMe(null))
    api.me().then(setMe, () => setMe(null))
  }, [])

  if (me === undefined) return <div className="notice login-wait">Loading...</div>
  if (me === null) return <Login onLogin={setMe} />
  return <Workspace me={me} onLogout={() => api.logout().finally(() => setMe(null))}
    onAccessChanged={() => api.me().then(setMe, () => setMe(null))} />
}

function pickProject(me: Me): number | undefined {
  const stored = storedProjectId()
  return me.projects.find((p) => p.id === stored)?.id ?? me.projects[0]?.id
}

function Workspace({ me, onLogout, onAccessChanged }: { me: Me; onLogout: () => void; onAccessChanged: () => void }) {
  const { route, navigate, setParams } = useRoute()
  const range = rangeFromParams(route.params)
  const [projectId, setProjectId] = useState(() => {
    const id = pickProject(me)
    if (id !== undefined) setProject(id)
    return id
  })

  const meta = useQuery((signal) => api.metadata(signal), [projectId])
  const err = meta.error ? (meta.loading ? undefined : meta.error) : undefined
  const status = meta.data && !meta.error ? 'ok' : err ? 'down' : 'checking'

  const project = me.projects.find((p) => p.id === projectId)
  const canManage = project?.role === 'owner' || project?.role === 'admin'

  useEffect(() => {
    if (project) return
    const id = pickProject(me)
    if (id !== undefined) setProject(id)
    setProjectId(id)
  }, [me, project])

  const changeProject = (id: number) => {
    setProject(id)
    setProjectId(id)
  }

  if (projectId === undefined || !project)
    return <div className="notice login-wait">Your account is not a member of any project. <button className="link" onClick={onLogout}>Sign out</button></div>

  const onQuery = (q: string) => setParams({ q })
  const onInsight = (id?: string) => setParams({ insight: id })
  const openPerson = (id?: string) => navigate('persons', id)

  return (
    <MetaContext.Provider value={meta.data}>
      <MetaDatalists />
      <header className="topbar">
        <div className="brand">ReapDeep</div>
        <nav className="tabs" aria-label="Tabs">
          {TABS.map((t) => (
            <a key={t.id} href={`#/${t.id}`} className={route.tab === t.id ? 'active' : ''}
              aria-current={route.tab === t.id ? 'page' : undefined}
              onClick={(e) => { e.preventDefault(); navigate(t.id) }}>
              {t.label}
            </a>
          ))}
        </nav>
        <div className="spacer" />
        {route.tab !== 'persons' && route.tab !== 'settings' && (
          <div className="range">
            <div className="segmented" role="group" aria-label="Date range">
              {RANGE_PRESETS.map((p) => (
                <button key={p.id} className={range.preset === p.id ? 'active' : ''}
                  onClick={() => setParams({ range: p.id as RangePreset, ...(p.id === 'custom' ? {} : { from: undefined, to: undefined }) })}>
                  {p.label}
                </button>
              ))}
            </div>
            {range.preset === 'custom' && (
              <>
                <input type="date" value={range.from ?? ''} onChange={(e) => setParams({ from: e.target.value })} aria-label="Start date" />
                <input type="date" value={range.to ?? ''} onChange={(e) => setParams({ to: e.target.value })} aria-label="End date" />
              </>
            )}
          </div>
        )}
        <div className="account">
          {me.projects.length > 1 ? (
            <select value={projectId} onChange={(e) => changeProject(Number(e.target.value))} aria-label="Project" data-testid="project-select">
              {me.projects.map((p) => <option key={p.id} value={p.id}>{p.organization_name} / {p.name}</option>)}
            </select>
          ) : (
            <span className="small" data-testid="project-name">{me.projects[0].name}</span>
          )}
          <span className={`status ${status}`} data-testid="connection-status" title={err}>
            <span className="status-dot" aria-hidden />
            {status === 'ok' ? 'Connected' : status === 'down' ? 'API unreachable' : 'Checking'}
          </span>
          <span className="small muted" data-testid="user-email">{me.user.email}</span>
          <button className="link" onClick={onLogout} data-testid="logout">Sign out</button>
        </div>
      </header>

      <main key={projectId}>
        {route.tab === 'dashboards' && <Dashboards id={route.id} params={route.params} range={range} openDashboard={(id) => navigate('dashboards', id)} />}
        {route.tab === 'trends' && <Trends params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
        {route.tab === 'funnels' && <Funnels params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
        {route.tab === 'events' && <Events params={route.params} range={range} onChange={onQuery} openPerson={openPerson} />}
        {route.tab === 'persons' && <Persons id={route.id} openPerson={openPerson} canManage={canManage} />}
        {route.tab === 'settings' && <Settings me={me} project={project!} onAccessChanged={onAccessChanged} />}
      </main>
    </MetaContext.Provider>
  )
}
