# @reapdeep/js

ReapDeep web SDK. ~2.6 KB gzip, no dependencies.

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

### Commerce and recommendations

Events about products carry `item_id` (the catalog key). Send one `purchase` event per item; `trackPurchase` does that and links them with `order_id`.

```js
const rd = init('pk_...', { apiHost: 'https://ingest.example.com', recommendHost: 'https://reco.example.com' })

rd.capture('product_viewed', { item_id: 'linen-shirt', price: 49 })
rd.capture('add_to_cart', { item_id: 'linen-shirt' })
rd.trackPurchase([{ item_id: 'linen-shirt', price: 49, quantity: 1 }, { item_id: 'wool-scarf', price: 29 }], 'order-1001', { currency: 'EUR' })

const similar = await rd.recommend({ model: 'co_viewed', itemId: 'linen-shirt', limit: 8 })
const popularShoes = await rd.recommend({ model: 'popular', category: 'shoes' })
```

Search events follow the same convention, so funnels such as search → result click → product view → purchase work on `item_id`:

```js
rd.capture('search_performed', { query: 'linen dress', results: 85, category: 'women', sort: 'relevance', page: 1 })
rd.capture('search_result_clicked', { query: 'linen dress', item_id: 'linen-shirt', position: 3 })
rd.capture('search_suggestion_clicked', { query: 'lin', type: 'product', value: 'linen-shirt' })
```

Models: `popular`, `trending`, `co_viewed` and `bought_together` (the last two need `itemId`). Only items that are in the catalog and in stock are returned; each item has a `source`, which is `popular` when a short item-based list was filled with popular items.

| Option | Default | |
|---|---|---|
| `apiHost` | | Ingestion API URL |
| `recommendHost` | | Recommender URL, needed for `recommend()` |
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
