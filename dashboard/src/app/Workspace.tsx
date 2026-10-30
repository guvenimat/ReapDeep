import { useEffect, useState } from 'react'
import { api, setProject, storedProjectId } from '../api/client'
import type { Me } from '../api/types'
import { CohortsContext, MetaContext, MetaDatalists } from '../components/editors'
import { EmptyState } from '../components/ui'
import { Catalog } from '../features/catalog/Catalog'
import { Cohorts } from '../features/cohorts/Cohorts'
import { Events } from '../features/events/Events'
import { Funnels } from '../features/funnels/Funnels'
import { Dashboards } from '../features/insights/Dashboards'
import { Paths } from '../features/paths/Paths'
import { Persons } from '../features/persons/Persons'
import { Retention } from '../features/retention/Retention'
import { Settings } from '../features/settings/Settings'
import { Trends } from '../features/trends/Trends'
import { useQuery } from '../lib/hooks'
import { RangePicker } from './RangePicker'
import { rangeFromParams, TABS, useRoute } from './route'
import { Sidebar } from './Sidebar'

function pickProject(me: Me): number | undefined {
  const stored = storedProjectId()
  const id = me.projects.find((p) => p.id === stored)?.id ?? me.projects[0]?.id
  if (id !== undefined) setProject(id)
  return id
}

export function Workspace({ me, onLogout, onAccessChanged }: { me: Me; onLogout: () => void; onAccessChanged: () => void }) {
  const { route, navigate, setParams } = useRoute()
  const range = rangeFromParams(route.params)
  const [projectId, setProjectId] = useState(() => pickProject(me))

  const meta = useQuery((signal) => api.metadata(signal), [projectId])
  const [cohortsVersion, setCohortsVersion] = useState(0)
  const cohorts = useQuery((signal) => api.cohorts(signal), [projectId, cohortsVersion])
  const err = meta.error && !meta.loading ? meta.error : undefined
  const status = meta.data && !meta.error ? 'ok' : err ? 'down' : 'checking'

  const project = me.projects.find((p) => p.id === projectId)
  const canManage = project?.role === 'owner' || project?.role === 'admin'
  const tab = TABS.find((t) => t.id === route.tab)!

  useEffect(() => {
    if (!project) setProjectId(pickProject(me))
  }, [me, project])

  if (projectId === undefined || !project)
    return (
      <div className="splash">
        <EmptyState icon="persons" title="No projects yet">
          Your account is not a member of any project. <button className="link" onClick={onLogout}>Sign out</button>
        </EmptyState>
      </div>
    )

  const changeProject = (id: number) => {
    setProject(id)
    setProjectId(id)
  }
  const onQuery = (q: string) => setParams({ q })
  const onInsight = (id?: string) => setParams({ insight: id })
  const openPerson = (id?: string) => navigate('persons', id)
  const showRange = route.tab !== 'persons' && route.tab !== 'settings' && route.tab !== 'cohorts' && route.tab !== 'catalog'

  return (
    <MetaContext.Provider value={meta.data}>
      <CohortsContext.Provider value={cohorts.data?.cohorts ?? []}>
        <MetaDatalists />
        <div className="shell">
          <Sidebar me={me} project={project} active={route.tab} status={status} statusError={err}
            onNavigate={(t) => navigate(t)} onProjectChange={changeProject} onLogout={onLogout} />
          <div className="main-col">
            <header className="topbar">
              <div className="topbar-title">
                <h1>{tab.label}</h1>
                <p>{tab.description}</p>
              </div>
              {showRange && <RangePicker range={range} onChange={setParams} />}
            </header>
            <main key={projectId}>
              {route.tab === 'dashboards' && <Dashboards id={route.id} params={route.params} range={range} openDashboard={(id) => navigate('dashboards', id)} />}
              {route.tab === 'trends' && <Trends params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'funnels' && <Funnels params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'retention' && <Retention params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'paths' && <Paths params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'events' && <Events params={route.params} range={range} onChange={onQuery} openPerson={openPerson} />}
              {route.tab === 'persons' && <Persons id={route.id} openPerson={openPerson} canManage={canManage} />}
              {route.tab === 'cohorts' && <Cohorts id={route.id} navigate={(id) => navigate('cohorts', id)} openPerson={openPerson}
                onChanged={() => setCohortsVersion((v) => v + 1)} />}
              {route.tab === 'catalog' && <Catalog id={route.id} canManage={canManage} navigate={(id) => navigate('catalog', id)} />}
              {route.tab === 'settings' && <Settings me={me} project={project} onAccessChanged={onAccessChanged} />}
            </main>
          </div>
        </div>
      </CohortsContext.Provider>
    </MetaContext.Provider>
  )
}
