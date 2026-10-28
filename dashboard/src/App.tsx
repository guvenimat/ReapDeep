import { useState } from 'react'
import { api, getApiKey, setApiKey } from './api'
import { Events } from './Events'
import { Funnels } from './Funnels'
import { Persons } from './Persons'
import { RANGE_PRESETS, rangeFromParams, TABS, useRoute, type RangePreset } from './route'
import { Trends } from './Trends'
import { MetaContext, MetaDatalists, useQuery } from './ui'

export function App() {
  const { route, navigate, setParams } = useRoute()
  const range = rangeFromParams(route.params)
  const [apiKey, setKey] = useState(getApiKey())
  const [draftKey, setDraftKey] = useState(apiKey)

  const meta = useQuery((signal) => api.metadata(signal), [apiKey])
  const err = meta.error ? (meta.loading ? undefined : meta.error) : undefined
  const status = meta.data && !meta.error ? 'ok' : err ? (/401|api key/i.test(err) ? 'invalid' : 'down') : 'checking'

  const commitKey = () => {
    const k = draftKey.trim()
    if (k && k !== apiKey) {
      setApiKey(k)
      setKey(k)
    }
  }

  const onQuery = (q: string) => setParams({ q })
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
        {route.tab !== 'persons' && (
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
        <div className="key">
          <input value={draftKey} onChange={(e) => setDraftKey(e.target.value)} onBlur={commitKey}
            onKeyDown={(e) => e.key === 'Enter' && commitKey()} aria-label="API key" spellCheck={false} />
          <span className={`status ${status}`} data-testid="connection-status" title={err}>
            <span className="status-dot" aria-hidden />
            {status === 'ok' ? 'Connected' : status === 'invalid' ? 'Invalid key' : status === 'down' ? 'API unreachable' : 'Checking'}
          </span>
        </div>
      </header>

      <main key={apiKey}>
        {route.tab === 'trends' && <Trends params={route.params} range={range} onChange={onQuery} />}
        {route.tab === 'funnels' && <Funnels params={route.params} range={range} onChange={onQuery} />}
        {route.tab === 'events' && <Events params={route.params} range={range} onChange={onQuery} openPerson={openPerson} />}
        {route.tab === 'persons' && <Persons id={route.id} openPerson={openPerson} />}
      </main>
    </MetaContext.Provider>
  )
}
