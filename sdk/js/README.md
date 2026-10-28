# @reapdeep/js

ReapDeep web SDK. ~2.4 KB gzip, no dependencies.

## Usage

```html
<script src="https://cdn.example.com/reapdeep.min.js"></script>
<script>
  const rd = reapdeep.init('pk_...', { apiHost: 'https://ingest.example.com' })
</script>
```

```js
import { init } from '@reapdeep/js'
const rd = init('pk_...', { apiHost: 'https://ingest.example.com' })

rd.capture('purchase', { product: 'Pro plan', amount: 499 })
rd.identify('user_123', { email: 'a@example.com' })
rd.register({ plan: 'pro' })
rd.reset()
```

| Option | Default | |
|---|---|---|
| `apiHost` | | Ingestion API URL |
| `autoPageview` | `true` | `$pageview` on load and on SPA navigation (`pushState`, `popstate`) |
| `flushInterval` | `3000` | Queue flush interval (ms) |
| `batchSize` | `50` | Flush immediately when the queue reaches this size |
| `sessionTimeoutMinutes` | `30` | New `$session_id` after inactivity (at most 24 h per session) |
| `debug` | `false` | Log to the console |

## Behavior

- Identity and session live in `localStorage` (`reapdeep_<key>`), with an in-memory fallback.
- Requests use `Content-Type: text/plain`, so browsers send no CORS preflight.
- 5xx / 408 / 429 / network errors are retried with exponential backoff (up to 30 s), keeping the same `event_id`. Other 4xx responses drop the batch.
- When the tab is hidden or the page unloads, the queue is sent with `sendBeacon` in chunks below 60 KB.
- `identify` sends `$anon_distinct_id` only on an anonymous to identified transition; two identified users are never merged.
- The queue holds at most 1000 events; the oldest are dropped beyond that.

## Development

```
npm install
npm test
npm run build            # dist/reapdeep.esm.js, dist/reapdeep.min.js, .d.ts
node demo/serve.mjs      # demo page at http://localhost:5500
.\demo\e2e.ps1           # headless Chrome -> ingestion -> Kafka -> StarRocks
```
