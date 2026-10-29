import { beacon, send, type QueuedEvent } from './transport'
import { createStorage, uuidv7, VERSION } from './util'

export interface Config {
  apiHost: string
  recommendHost?: string
  autoPageview?: boolean
  flushInterval?: number
  batchSize?: number
  sessionTimeoutMinutes?: number
  debug?: boolean
}

export type Properties = Record<string, unknown>

export type RecommendationModel = 'popular' | 'trending' | 'co_viewed' | 'bought_together'

export interface RecommendOptions {
  model?: RecommendationModel
  itemId?: string
  category?: string
  limit?: number
}

export interface RecommendedItem {
  item_id: string
  title: string
  category: string
  price: number | null
  currency: string
  url: string
  image_url: string
  score: number
  source: RecommendationModel
}

export interface PurchasedItem extends Properties {
  item_id: string
  price?: number
  quantity?: number
}

interface State {
  distinctId: string
  identified: boolean
  sessionId: string
  sessionStart: number
  lastActivity: number
  superProps: Properties
}

const MAX_QUEUE = 1000
const MAX_SESSION_MS = 24 * 60 * 60 * 1000
const MAX_BACKOFF_MS = 30_000
const UTM_PARAMS = ['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content']

export class ReapDeep {
  private readonly url: string
  private readonly cfg: Required<Config>
  private readonly storage: ReturnType<typeof createStorage>
  private state: State
  private queue: QueuedEvent[] = []
  private sending = false
  private retries = 0
  private timer: ReturnType<typeof setTimeout> | undefined
  private lastPageviewUrl: string | undefined

  constructor(private readonly apiKey: string, config: Config) {
    this.cfg = { recommendHost: '', autoPageview: true, flushInterval: 3000, batchSize: 50, sessionTimeoutMinutes: 30, debug: false, ...config }
    this.url = this.cfg.apiHost.replace(/\/+$/, '') + '/capture'
    this.storage = createStorage(`reapdeep_${apiKey}`)
    this.state = this.storage.get<State>() ?? this.newState()
    this.storage.set(this.state)

    if (typeof window === 'undefined') return

    const unload = () => this.flushOnUnload()
    window.addEventListener('pagehide', unload)
    document.addEventListener('visibilitychange', () => document.visibilityState === 'hidden' && unload())

    if (this.cfg.autoPageview) {
      this.pageview()
      this.trackHistory()
    }
  }

  capture(event: string, properties?: Properties): void {
    if (typeof window === 'undefined' || !event) return
    const props: Properties = {
      ...this.contextProps(),
      ...this.state.superProps,
      ...properties,
      $session_id: this.sessionId(),
      $lib: 'web',
      $lib_version: VERSION,
    }
    this.enqueue({
      event,
      distinct_id: this.state.distinctId,
      event_id: uuidv7(),
      timestamp: new Date().toISOString(),
      properties: props,
    })
  }

  pageview(properties?: Properties): void {
    this.lastPageviewUrl = location.href
    this.capture('$pageview', properties)
  }

  trackPurchase(items: PurchasedItem[], orderId: string, properties?: Properties): void {
    for (const item of items) if (item?.item_id) this.capture('purchase', { ...properties, ...item, order_id: orderId })
  }

  async recommend(options: RecommendOptions = {}): Promise<RecommendedItem[]> {
    if (!this.cfg.recommendHost) throw new Error('reapdeep: set recommendHost to use recommend()')
    const query = new URLSearchParams({ api_key: this.apiKey, model: options.model ?? 'popular' })
    if (options.itemId) query.set('item_id', options.itemId)
    if (options.category) query.set('category', options.category)
    if (options.limit) query.set('limit', String(options.limit))
    const res = await fetch(`${this.cfg.recommendHost.replace(/\/+$/, '')}/recommend?${query}`)
    if (!res.ok) throw new Error(`reapdeep: recommend failed with HTTP ${res.status}`)
    return ((await res.json()) as { items: RecommendedItem[] }).items
  }

  identify(distinctId: string, personProperties?: Properties): void {
    if (!distinctId) return
    const previous = this.state.distinctId
    if (distinctId === previous) {
      if (personProperties) this.capture('$set', { $set: personProperties })
      return
    }
    const anon = this.state.identified ? undefined : previous
    this.state.distinctId = distinctId
    this.state.identified = true
    this.save()
    this.capture('$identify', { $anon_distinct_id: anon, $set: personProperties })
  }

  reset(): void {
    this.state = this.newState()
    this.save()
  }

  register(properties: Properties): void {
    this.state.superProps = { ...this.state.superProps, ...properties }
    this.save()
  }

  unregister(name: string): void {
    delete this.state.superProps[name]
    this.save()
  }

  getDistinctId(): string {
    return this.state.distinctId
  }

  async flush(): Promise<void> {
    if (this.sending || this.queue.length === 0) return
    this.sending = true
    clearTimeout(this.timer)
    this.timer = undefined

    const batch = this.queue.splice(0, this.cfg.batchSize)
    const result = await send(this.url, this.apiKey, batch)
    this.sending = false

    if (result === 'retry') {
      this.queue.unshift(...batch)
      this.trimQueue()
      this.retries++
      this.log('send failed, will retry', this.retries)
    } else {
      if (result === 'drop') this.log('rejected by server, dropped', batch.length)
      this.retries = 0
      if (this.queue.length >= this.cfg.batchSize) return this.flush()
    }
    this.schedule()
  }

  private enqueue(e: QueuedEvent): void {
    this.queue.push(e)
    this.trimQueue()
    this.log('queued', e.event, e.properties)
    if (this.queue.length >= this.cfg.batchSize && this.retries === 0) void this.flush()
    else this.schedule()
  }

  private schedule(): void {
    if (this.timer !== undefined || this.queue.length === 0) return
    const delay = this.retries > 0 ? Math.min(MAX_BACKOFF_MS, 1000 * 2 ** this.retries) : this.cfg.flushInterval
    this.timer = setTimeout(() => {
      this.timer = undefined
      void this.flush()
    }, delay)
  }

  private flushOnUnload(): void {
    this.queue = beacon(this.url, this.apiKey, this.queue)
  }

  private trimQueue(): void {
    if (this.queue.length > MAX_QUEUE) this.queue.splice(0, this.queue.length - MAX_QUEUE)
  }

  private sessionId(): string {
    const now = Date.now()
    const s = this.state
    if (now - s.lastActivity > this.cfg.sessionTimeoutMinutes * 60_000 || now - s.sessionStart > MAX_SESSION_MS) {
      s.sessionId = uuidv7()
      s.sessionStart = now
    }
    s.lastActivity = now
    this.save()
    return s.sessionId
  }

  private contextProps(): Properties {
    const props: Properties = {
      $current_url: location.href,
      $host: location.host,
      $pathname: location.pathname,
      $referrer: document.referrer || '$direct',
      $referring_domain: referringDomain(document.referrer),
      $screen_width: screen.width,
      $screen_height: screen.height,
    }
    const params = new URLSearchParams(location.search)
    for (const p of UTM_PARAMS) {
      const v = params.get(p)
      if (v) props[p] = v
    }
    return props
  }

  private trackHistory(): void {
    const onChange = () => {
      if (location.href !== this.lastPageviewUrl) this.pageview()
    }
    const push = history.pushState
    history.pushState = function (this: History, ...args: Parameters<History['pushState']>) {
      push.apply(this, args)
      onChange()
    }
    window.addEventListener('popstate', onChange)
  }

  private newState(): State {
    const now = Date.now()
    return { distinctId: uuidv7(), identified: false, sessionId: uuidv7(), sessionStart: now, lastActivity: now, superProps: {} }
  }

  private save(): void {
    this.storage.set(this.state)
  }

  private log(...args: unknown[]): void {
    if (this.cfg.debug) console.log('[reapdeep]', ...args)
  }
}

function referringDomain(referrer: string): string {
  if (!referrer) return '$direct'
  try {
    return new URL(referrer).host
  } catch {
    return '$direct'
  }
}

const instances: Record<string, ReapDeep> = {}
export function init(apiKey: string, config: Config): ReapDeep {
  return (instances[apiKey] ??= new ReapDeep(apiKey, config))
}
