import { useState, type ChangeEvent } from 'react'
import { api, errorMessage } from '../../api/client'
import type { CatalogItem, HitRate, ModelRun, RecommendationModel, RecommendedCatalogItem } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card, FormMessage, IconButton, StatTile, Status } from '../../components/ui'
import { ago, num, pct } from '../../lib/format'
import { useDebounced, useQuery } from '../../lib/hooks'
import { itemsFromCsv } from './csv'

const MODELS: { id: RecommendationModel; label: string; description: string }[] = [
  { id: 'co_viewed', label: 'Viewed together', description: 'People who looked at this also looked at' },
  { id: 'bought_together', label: 'Bought together', description: 'People who bought this also bought' },
  { id: 'popular', label: 'Popular', description: 'Most engaged items, last 30 days' },
  { id: 'trending', label: 'Trending', description: 'Growing fastest this week' },
]

const UPLOAD_CHUNK = 5000

export function Catalog({ id, canManage, navigate }: { id?: string; canManage: boolean; navigate: (id?: string) => void }) {
  const [version, setVersion] = useState(0)
  return (
    <div className={id ? 'split' : 'stack'}>
      <div className="stack">
        {!id && <Overview version={version} />}
        <ItemList version={version} selected={id} navigate={navigate} canManage={canManage} onUploaded={() => setVersion((v) => v + 1)} />
      </div>
      {id && <ItemDetail key={id} itemId={id} onClose={() => navigate(undefined)} />}
    </div>
  )
}

function Overview({ version }: { version: number }) {
  const models = useQuery((signal) => api.recommendationModels(signal), [version])
  const runs = new Map((models.data?.models ?? []).map((m) => [m.model, m]))
  return (
    <>
      <div className="stats">
        {MODELS.map((m) => <ModelTile key={m.id} label={m.label} description={m.description} run={runs.get(m.id)} />)}
      </div>
      <Quality />
    </>
  )
}

function ModelTile({ label, description, run }: { label: string; description: string; run?: ModelRun }) {
  const global = run?.model === 'popular' || run?.model === 'trending'
  return (
    <StatTile label={label} value={run ? (global ? 'Ready' : num(run.source_items)) : '—'}
      hint={run ? `${global ? '' : 'items with suggestions · '}${ago(run.calculated_at)} · ${num(run.calculation_ms)} ms` : description} />
  )
}

function Quality() {
  const [result, setResult] = useState<HitRate>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const measure = async () => {
    setBusy(true)
    setError(undefined)
    try {
      setResult(await api.evaluateRecommendations(10))
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  const lift = result && result.popular_hit_rate > 0 ? result.co_viewed_hit_rate / result.popular_hit_rate - 1 : undefined
  return (
    <Card title={<><Icon name="sparkles" />Recommendation quality</>}
      meta="Trains on the 30 days before last week, then checks whether each person's next item last week was in the top 10"
      actions={<button type="button" className="primary" onClick={measure} disabled={busy} data-testid="measure-quality">{busy ? 'Measuring…' : 'Measure now'}</button>}>
      <FormMessage error={error} />
      {result ? (
        <div className="quality" data-testid="quality-result">
          <QualityBar label="Viewed together" rate={result.co_viewed_hit_rate} hits={result.co_viewed_hits} cases={result.cases} accent />
          <QualityBar label="Popular (baseline)" rate={result.popular_hit_rate} hits={result.popular_hits} cases={result.cases} />
          <p className="footnote">
            {num(result.cases)} test cases · suggestions available for {pct(result.cases ? result.covered / result.cases : 0)} of them
            {lift !== undefined && <> · <strong className={lift >= 0 ? 'good' : 'form-message error'}>{lift >= 0 ? '+' : ''}{pct(lift)}</strong> against the baseline</>}
            {' '}· {num(Math.round(result.took_ms))} ms
          </p>
        </div>
      ) : (
        <p className="editor-hint">Hit rate at 10: the share of next items that the model would have shown. Runs on demand in StarRocks.</p>
      )}
    </Card>
  )
}

function QualityBar({ label, rate, hits, cases, accent }: { label: string; rate: number; hits: number; cases: number; accent?: boolean }) {
  return (
    <div className="quality-row">
      <span className="quality-label">{label}</span>
      <div className="path-bar-track quality-track"><div className={`path-bar${accent ? '' : ' muted-bar'}`} style={{ width: `${Math.max(1, rate * 100)}%` }} /></div>
      <span className="quality-value"><strong>{pct(rate)}</strong> <span className="muted small">{num(hits)}/{num(cases)}</span></span>
    </div>
  )
}

function ItemList({ version, selected, navigate, canManage, onUploaded }: {
  version: number; selected?: string; navigate: (id?: string) => void; canManage: boolean; onUploaded: () => void
}) {
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState('')
  const debounced = useDebounced(search)
  const list = useQuery((signal) => api.items({ search: debounced.trim(), category, limit: 200 }, signal), [debounced, category, version])
  const d = list.data

  return (
    <>
      <div className="toolbar">
        <div className="search-input grow">
          <Icon name="search" />
          <input placeholder="Search by item_id or title" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search items" />
        </div>
        <select value={category} onChange={(e) => setCategory(e.target.value)} aria-label="Category">
          <option value="">All categories</option>
          {d?.categories.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
        <span className="toolbar-meta">{d && `${num(d.total)} items · ${num(d.in_stock)} in stock`}</span>
        {canManage && <Upload onUploaded={onUploaded} />}
      </div>
      <Status state={list} empty={d?.items.length === 0} emptyTitle="No items"
        emptyText="Upload a CSV with item_id, title, category, price, in_stock, url, or sync it with a catalog_write API key (PUT /api/v1/items).">
        <Card className="flush">
          <div className="table-wrap">
            <table data-testid="items-table" className={selected ? 'compact' : undefined}>
              <thead><tr><th>Item</th><th>Category</th><th>Updated</th><th className="num">Price</th><th>Stock</th></tr></thead>
              <tbody>
                {d?.items.map((i) => (
                  <tr key={i.item_id} className={`clickable${i.item_id === selected ? ' open' : ''}`} onClick={() => navigate(i.item_id)}>
                    <td>
                      <div className="person-cell">
                        <span className="item-thumb" aria-hidden><Icon name="catalog" /></span>
                        <div><div className="strong">{i.title}</div><div className="mono muted small">{i.item_id}</div></div>
                      </div>
                    </td>
                    <td>{i.category && <Badge>{i.category}</Badge>}</td>
                    <td className="nowrap muted">{ago(i.updated_at)}</td>
                    <td className="num tabular nowrap">{price(i)}</td>
                    <td className="nowrap">{i.in_stock ? <Badge tone="good">In stock</Badge> : <Badge tone="muted">Out of stock</Badge>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      </Status>
    </>
  )
}

function Upload({ onUploaded }: { onUploaded: () => void }) {
  const [state, setState] = useState<{ busy?: boolean; message?: string; error?: string }>({})
  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    const { items, errors } = itemsFromCsv(await file.text())
    if (errors.length) { setState({ error: errors.slice(0, 3).join('; ') + (errors.length > 3 ? ` (+${errors.length - 3} more)` : '') }); return }
    setState({ busy: true })
    try {
      let total = 0
      for (let i = 0; i < items.length; i += UPLOAD_CHUNK) total += (await api.upsertItems(items.slice(i, i + UPLOAD_CHUNK))).upserted
      setState({ message: `${num(total)} items saved` })
      onUploaded()
    } catch (err) {
      setState({ error: errorMessage(err) })
    }
  }
  return (
    <>
      <label className="file-button primary-file">
        <Icon name="upload" />{state.busy ? 'Uploading…' : 'Upload CSV'}
        <input type="file" accept=".csv,text/csv" onChange={onFile} disabled={state.busy} data-testid="catalog-upload" />
      </label>
      {(state.message || state.error) && <span className={`form-message ${state.error ? 'error' : 'good'}`}>{state.error ?? state.message}</span>}
    </>
  )
}

function ItemDetail({ itemId, onClose }: { itemId: string; onClose: () => void }) {
  const detail = useQuery((signal) => api.item(itemId, signal), [itemId])
  const i = detail.data?.item
  const snippet = `curl "http://localhost:8002/recommend?api_key=<public key>&model=co_viewed&item_id=${encodeURIComponent(itemId)}&limit=8"`
  return (
    <aside className="card detail-panel" data-testid="item-detail">
      <div className="person-hero">
        <span className="item-thumb lg" aria-hidden><Icon name="catalog" size={22} /></span>
        <div className="person-hero-text">
          <h2>{i?.title ?? itemId}</h2>
          {i && (
            <div className="row">
              {i.in_stock ? <Badge tone="good">In stock</Badge> : <Badge tone="muted">Out of stock</Badge>}
              {i.category && <Badge>{i.category}</Badge>}
              <span className="muted small">{price(i)}</span>
            </div>
          )}
        </div>
        <IconButton icon="close" label="Close" onClick={onClose} />
      </div>
      <Status state={detail}>
        {detail.data && (
          <>
            <dl className="kv">
              <dt>item_id</dt><dd className="mono">{itemId}</dd>
              {i?.url && <><dt>url</dt><dd><a href={i.url} target="_blank" rel="noreferrer">{i.url}</a></dd></>}
            </dl>
            <RecommendationList title="Viewed together" items={detail.data.co_viewed} testId="co-viewed" />
            <RecommendationList title="Bought together" items={detail.data.bought_together} testId="bought-together" />
            <div>
              <div className="editor-title">Serve it on your site</div>
              <pre className="json">{snippet}</pre>
              <p className="editor-hint">The public Recommender skips out-of-stock items and fills short lists with popular items from the same category.</p>
            </div>
          </>
        )}
      </Status>
    </aside>
  )
}

function RecommendationList({ title, items, testId }: { title: string; items: RecommendedCatalogItem[]; testId: string }) {
  const max = Math.max(...items.map((r) => r.score), 0.0001)
  return (
    <div className="rec-list" data-testid={testId}>
      <div className="editor-title">{title} {items.length > 0 && <span className="muted">· {items.length}</span>}</div>
      {items.length === 0 ? <p className="editor-hint">Not enough data yet; the public API falls back to popular items.</p> : items.map((r) => (
        <div key={r.item_id} className={`path-node${r.in_stock ? '' : ' out-of-stock'}`}>
          <div className="path-node-label">
            <span className="truncate">{r.title} <span className="muted small">{r.category}</span>{!r.in_stock && <span className="muted small"> · out of stock</span>}</span>
            <span className="tabular muted">{r.score.toFixed(3)}</span>
          </div>
          <div className="path-bar-track"><div className="path-bar" style={{ width: `${Math.max(2, (r.score / max) * 100)}%` }} /></div>
        </div>
      ))}
    </div>
  )
}

const price = (i: CatalogItem) => (i.price === null ? '—' : `${i.price.toLocaleString('en-US', { minimumFractionDigits: 2 })} ${i.currency}`)
