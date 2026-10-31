# ADR 0005: A separate public Recommender that serves from memory

Status: accepted

## Context

Recommendations are requested by storefront pages, often directly from browsers, with a budget of a few milliseconds. The models scan 30 to 90 days of events. The Query API is protected by secret keys and sessions and does heavy StarRocks work.

## Decision

Models (popular, trending, viewed together, bought together) are computed in the background and stored in PostgreSQL as `item_id` and score only. A separate Recommender service loads them into memory, joins live catalog fields (title, price, stock) at request time and is reachable with the project's public key and open CORS. Personal `for_you` lists are scored per request from a short stored history, and the request must carry `HMAC(secret, distinct_id.expires)` so nobody can read another person's history by guessing an id.

## Consequences

- An out-of-stock product disappears from every list at once, not at the next model refresh.
- The public endpoint cannot reach analytics data or StarRocks.
- A hit-rate check against a popularity baseline runs from the Query API, so model quality can be measured on real data.
