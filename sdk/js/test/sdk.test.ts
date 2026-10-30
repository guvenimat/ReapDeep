import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReapDeep } from '../src/index'
import { beacon, MAX_BODY_BYTES, type QueuedEvent } from '../src/transport'

type Body = { api_key: string; sent_at: string; batch: QueuedEvent[] }

let fetchMock: ReturnType<typeof vi.fn>
const sentBatches = (): Body[] => fetchMock.mock.calls.map((c) => JSON.parse(c[1].body))
const sentEvents = () => sentBatches().flatMap((b) => b.batch)

function respond(status: number) {
  fetchMock.mockResolvedValue({ ok: status >= 200 && status < 300, status })
}

function client(config: Partial<ConstructorParameters<typeof ReapDeep>[1]> = {}) {
  return new ReapDeep('pk_test', { apiHost: 'http://ingest.local/', autoPageview: false, ...config })
}

beforeEach(() => {
  vi.useFakeTimers()
  localStorage.clear()
  history.replaceState(null, '', '/products?utm_source=google&utm_campaign=winter')
  fetchMock = vi.fn()
  respond(202)
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('capture', () => {
  it('sends queued events in the expected format after flushInterval', async () => {
    const rd = client()
    rd.register({ plan: 'pro' })
    rd.capture('button_click', { button: 'purchase' })
    expect(fetchMock).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(3000)

    expect(fetchMock).toHaveBeenCalledOnce()
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('http://ingest.local/capture')
    expect(init.headers['Content-Type']).toBe('text/plain')
    const body: Body = JSON.parse(init.body)
    expect(body.api_key).toBe('pk_test')
    expect(body.sent_at).toBeTruthy()
    const e = body.batch[0]
    expect(e.event).toBe('button_click')
    expect(e.distinct_id).toBe(rd.getDistinctId())
    expect(e.event_id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(e.properties).toMatchObject({
      button: 'purchase',
      plan: 'pro',
      $pathname: '/products',
      $referrer: '$direct',
      $referring_domain: '$direct',
      utm_source: 'google',
      utm_campaign: 'winter',
      $lib: 'web',
    })
    expect(e.properties.$session_id).toBeTruthy()
  })

  it('flushes immediately when batchSize is reached', async () => {
    const rd = client({ batchSize: 2 })
    rd.capture('a')
    rd.capture('b')
    await vi.advanceTimersByTimeAsync(0)
    expect(sentEvents().map((e) => e.event)).toEqual(['a', 'b'])
  })
})

describe('pageview', () => {
  it('captures $pageview on load and on SPA navigation, but not twice for the same URL', async () => {
    client({ autoPageview: true })
    history.pushState(null, '', '/cart')
    history.pushState(null, '', '/cart')
    await vi.advanceTimersByTimeAsync(3000)
    expect(sentEvents().map((e) => [e.event, e.properties.$pathname])).toEqual([
      ['$pageview', '/products'],
      ['$pageview', '/cart'],
    ])
  })
})

describe('identify / reset', () => {
  it('sends $anon_distinct_id only on anonymous to identified transition and never merges two identified users', async () => {
    const rd = client()
    const anon = rd.getDistinctId()

    rd.identify('user_1', { email: 'a@example.com' })
    rd.identify('user_2')
    await vi.advanceTimersByTimeAsync(3000)

    const [first, second] = sentEvents()
    expect(first).toMatchObject({ event: '$identify', distinct_id: 'user_1' })
    expect(first.properties).toMatchObject({ $anon_distinct_id: anon, $set: { email: 'a@example.com' } })
    expect(second).toMatchObject({ event: '$identify', distinct_id: 'user_2' })
    expect(second.properties.$anon_distinct_id).toBeUndefined()
  })

  it('reset creates a new anonymous id and session', () => {
    const rd = client()
    rd.identify('user_1')
    rd.reset()
    expect(rd.getDistinctId()).not.toBe('user_1')
  })

  it('keeps the identity across page reloads (localStorage)', () => {
    const id = client().getDistinctId()
    expect(client().getDistinctId()).toBe(id)
  })
})

describe('session', () => {
  it('starts a new $session_id after 30 minutes of inactivity', async () => {
    const rd = client()
    rd.capture('a')
    vi.advanceTimersByTime(10 * 60_000)
    rd.capture('b')
    vi.advanceTimersByTime(31 * 60_000)
    rd.capture('c')
    await vi.advanceTimersByTimeAsync(3000)

    const [a, b, c] = sentEvents().map((e) => e.properties.$session_id)
    expect(a).toBe(b)
    expect(c).not.toBe(a)
  })
})

describe('error handling', () => {
  it('keeps events on 503 or network errors and retries with backoff', async () => {
    const rd = client()
    fetchMock.mockResolvedValueOnce({ ok: false, status: 503 }).mockRejectedValueOnce(new TypeError('offline'))
    rd.capture('important')

    await vi.advanceTimersByTimeAsync(3000)
    await vi.advanceTimersByTimeAsync(2000)
    await vi.advanceTimersByTimeAsync(4000)

    expect(fetchMock).toHaveBeenCalledTimes(3)
    const ids = sentBatches().map((b) => b.batch[0].event_id)
    expect(new Set(ids).size).toBe(1)
  })

  it('drops events on permanent errors such as 401 without retrying', async () => {
    const rd = client()
    respond(401)
    rd.capture('a')
    await vi.advanceTimersByTimeAsync(60_000)
    expect(fetchMock).toHaveBeenCalledOnce()
  })
})

describe('page unload', () => {
  it('sends the queue with sendBeacon in chunks below 60 KB', () => {
    const sendBeacon = vi.fn().mockReturnValue(true)
    vi.stubGlobal('navigator', { sendBeacon })
    const big = 'é'.repeat(5000)
    const events: QueuedEvent[] = Array.from({ length: 20 }, (_, i) => ({
      event: 'e' + i, distinct_id: 'd', event_id: String(i), timestamp: '', properties: { big },
    }))

    expect(beacon('http://x/capture', 'pk', events)).toEqual([])

    const bodies = sendBeacon.mock.calls.map((c) => c[1] as string)
    expect(bodies.length).toBeGreaterThan(1)
    for (const b of bodies) expect(new TextEncoder().encode(b).length).toBeLessThanOrEqual(MAX_BODY_BYTES)
    expect(bodies.flatMap((b) => JSON.parse(b).batch.map((e: QueuedEvent) => e.event_id))).toEqual(events.map((e) => e.event_id))
  })

  it('keeps unsent events when sendBeacon refuses', () => {
    vi.stubGlobal('navigator', { sendBeacon: vi.fn().mockReturnValue(false) })
    const events = [{ event: 'a', distinct_id: 'd', event_id: '1', timestamp: '', properties: {} }]
    expect(beacon('http://x/capture', 'pk', events)).toEqual(events)
  })

  it('flushes the queue on pagehide', async () => {
    const sendBeacon = vi.fn().mockReturnValue(true)
    vi.stubGlobal('navigator', { sendBeacon })
    const rd = client()
    rd.capture('before_leave')
    window.dispatchEvent(new Event('pagehide'))
    const beaconed = sendBeacon.mock.calls.flatMap((c) => JSON.parse(c[1]).batch.map((e: QueuedEvent) => e.event))
    expect(beaconed).toContain('before_leave')
    await vi.advanceTimersByTimeAsync(3000)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('commerce', () => {
  it('trackPurchase sends one purchase event per item with a shared order_id', async () => {
    const rd = client()
    rd.trackPurchase([{ item_id: 'sku-1', price: 10, quantity: 2 }, { item_id: 'sku-2', price: 5 }], 'order-9', { currency: 'EUR' })
    await vi.advanceTimersByTimeAsync(3000)

    const purchases = sentEvents().filter((e) => e.event === 'purchase')
    expect(purchases.map((e) => e.properties.item_id)).toEqual(['sku-1', 'sku-2'])
    expect(purchases.every((e) => e.properties.order_id === 'order-9' && e.properties.currency === 'EUR')).toBe(true)
    expect(purchases[0].properties.quantity).toBe(2)
  })

  it('recommend calls the recommender with the public key and returns its items', async () => {
    const rd = client({ recommendHost: 'http://reco.local/' })
    fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ items: [{ item_id: 'sku-2', score: 0.4 }] }) })

    const items = await rd.recommend({ model: 'co_viewed', itemId: 'sku 1', limit: 4 })

    const url = new URL(fetchMock.mock.calls[0][0])
    expect(url.origin + url.pathname).toBe('http://reco.local/recommend')
    expect(Object.fromEntries(url.searchParams)).toEqual({ api_key: 'pk_test', model: 'co_viewed', item_id: 'sku 1', limit: '4' })
    expect(fetchMock.mock.calls[0][1]).toBeUndefined()
    expect(items.map((i) => i.item_id)).toEqual(['sku-2'])
  })

  it('recommend fails clearly without recommendHost or on an error response', async () => {
    await expect(client().recommend()).rejects.toThrow('recommendHost')
    const rd = client({ recommendHost: 'http://reco.local' })
    fetchMock.mockResolvedValueOnce({ ok: false, status: 401, json: async () => ({}) })
    await expect(rd.recommend()).rejects.toThrow('HTTP 401')
  })
})
