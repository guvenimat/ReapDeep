import { useEffect, useState, useMemo } from 'react'
import { api, setProject, storedProjectId } from '../api/client'
import type { Me } from '../api/types'
import { CohortsContext, MetaContext, ActionsContext } from '../components/editors'
import { EmptyState } from '../components/ui'
import { Alerts } from '../features/alerts/Alerts'
import { Catalog } from '../features/catalog/Catalog'
import { Cohorts } from '../features/cohorts/Cohorts'
import { Events } from '../features/events/Events'
import { Experiments } from '../features/experiments/Experiments'
import { Flags } from '../features/flags/Flags'
import { Funnels } from '../features/funnels/Funnels'
import { Lifecycle } from '../features/lifecycle/Lifecycle'
import { Dashboards } from '../features/insights/Dashboards'
import { Paths } from '../features/paths/Paths'
import { Persons } from '../features/persons/Persons'
import { Retention } from '../features/retention/Retention'
import { Rfm } from '../features/rfm/Rfm'
import { Settings } from '../features/settings/Settings'
import { Trends } from '../features/trends/Trends'
import { Web } from '../features/web/Web'
import { useQuery } from '../lib/hooks'
import { RangePicker } from './RangePicker'
import { rangeFromParams, TABS, useRoute } from './route'
import { Sidebar } from './Sidebar'
import { AiPage } from '../features/ai/Ai'
import { AiFeaturesContext, PlatformPage } from '../features/ai/AiAccess'

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
  const [aiVersion, setAiVersion] = useState(0)
  const aiAccess = useQuery((signal) => api.aiAccess(signal), [projectId, aiVersion])
  const aiContext = useMemo(() => ({ features: aiAccess.data?.effective ?? [], reload: () => setAiVersion((v) => v + 1) }), [aiAccess.data])
  const [actionsVersion, setActionsVersion] = useState(0)
  const actions = useQuery((signal) => api.actions(signal), [projectId, actionsVersion])
  const actionsContext = useMemo(() => ({ actions: actions.data?.actions ?? [], reload: () => setActionsVersion((v) => v + 1) }), [actions.data])
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
  const showRange = route.tab !== 'persons' && route.tab !== 'settings' && route.tab !== 'cohorts' && route.tab !== 'catalog' && route.tab !== 'rfm' && route.tab !== 'alerts' && route.tab !== 'flags' && route.tab !== 'experiments' && route.tab !== 'ai' && route.tab !== 'platform'

  return (
    <MetaContext.Provider value={meta.data}>
      <CohortsContext.Provider value={cohorts.data?.cohorts ?? []}>
      <ActionsContext.Provider value={actionsContext}>
      <AiFeaturesContext.Provider value={aiContext}>
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
              {route.tab === 'web' && <Web params={route.params} range={range} onChange={onQuery} />}
              {route.tab === 'trends' && <Trends params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'funnels' && <Funnels params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'retention' && <Retention params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'paths' && <Paths params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'lifecycle' && <Lifecycle params={route.params} range={range} onChange={onQuery} onInsight={onInsight} />}
              {route.tab === 'rfm' && <Rfm params={route.params} onChange={onQuery} onInsight={onInsight} onCohortCreated={() => setCohortsVersion((v) => v + 1)} />}
              {route.tab === 'alerts' && <Alerts canManage={canManage} />}
              {route.tab === 'flags' && <Flags canManage={canManage} />}
              {route.tab === 'ai' && <AiPage canManage={canManage} />}
              {route.tab === 'platform' && (me.platform_admin ? <PlatformPage /> : <p className="editor-hint">Only platform administrators can open this page.</p>)}
              {route.tab === 'experiments' && <Experiments id={route.id} canManage={canManage} navigate={(id) => navigate('experiments', id)} />}
              {route.tab === 'events' && <Events params={route.params} range={range} onChange={onQuery} openPerson={openPerson} />}
              {route.tab === 'persons' && <Persons id={route.id} openPerson={openPerson} canManage={canManage} />}
              {route.tab === 'cohorts' && <Cohorts id={route.id} navigate={(id) => navigate('cohorts', id)} openPerson={openPerson}
                onChanged={() => setCohortsVersion((v) => v + 1)} />}
              {route.tab === 'catalog' && <Catalog id={route.id} canManage={canManage} navigate={(id) => navigate('catalog', id)} />}
              {route.tab === 'settings' && <Settings me={me} project={project} onAccessChanged={onAccessChanged} />}
            </main>
          </div>
        </div>
      </AiFeaturesContext.Provider>
      </ActionsContext.Provider>
      </CohortsContext.Provider>
    </MetaContext.Provider>
  )
}
