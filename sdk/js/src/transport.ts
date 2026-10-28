export interface QueuedEvent {
  event: string
  distinct_id: string
  event_id: string
  timestamp: string
  properties: Record<string, unknown>
}

export type SendResult = 'ok' | 'retry' | 'drop'

export const MAX_BODY_BYTES = 60_000
const ENVELOPE_BYTES = 200

const encoder = new TextEncoder()
const byteLength = (s: string) => encoder.encode(s).length

export function body(apiKey: string, batch: QueuedEvent[]): string {
  return JSON.stringify({ api_key: apiKey, sent_at: new Date().toISOString(), batch })
}

export async function send(url: string, apiKey: string, batch: QueuedEvent[]): Promise<SendResult> {
  const payload = body(apiKey, batch)
  try {
    const res = await fetch(url, {
      method: 'POST',
      body: payload,
      headers: { 'Content-Type': 'text/plain' },
      keepalive: byteLength(payload) < MAX_BODY_BYTES,
    })
    if (res.ok) return 'ok'
    if (res.status >= 400 && res.status < 500 && res.status !== 408 && res.status !== 429) return 'drop'
    return 'retry'
  } catch {
    return 'retry'
  }
}

export function beacon(url: string, apiKey: string, events: QueuedEvent[]): QueuedEvent[] {
  if (events.length === 0) return []
  if (typeof navigator === 'undefined' || !navigator.sendBeacon) return events

  let start = 0
  let size = ENVELOPE_BYTES
  for (let i = 0; i <= events.length; i++) {
    const s = i < events.length ? byteLength(JSON.stringify(events[i])) + 1 : 0
    if (i === events.length || (i > start && size + s > MAX_BODY_BYTES)) {
      if (!navigator.sendBeacon(url, body(apiKey, events.slice(start, i)))) return events.slice(start)
      start = i
      size = ENVELOPE_BYTES
    }
    size += s
  }
  return []
}
