# ADR 0002: A 202 means the event is durably in Kafka

Status: accepted

## Context

The ingestion API has to take 50K events per second with a p99 under 10 ms, and a customer must be able to trust that an accepted event will be counted. Writing straight to the analytics store would tie ingestion latency to query load.

## Decision

`POST /capture` answers `202` only after Kafka has acknowledged every event in the batch (`acks=all`, idempotent producer, `linger.ms=5`, LZ4). If Kafka does not acknowledge within 10 seconds the client gets `503` and the SDK retries with the same `event_id`. Every event gets a UUIDv7 `event_id`; duplicates from retries are removed at query time, and events that can never be loaded go to a dead-letter topic with a classified reason.

The ingestion service is Native AOT and keeps no state that needs a database on the request path; event and property names for autocomplete are written to PostgreSQL in the background.

## Consequences

- At least once, never lost: a retry can duplicate, which queries absorb.
- Micro-batching across concurrent requests keeps p99 low despite `acks=all`.
- StarRocks, the person worker and the recommender all consume the same topic independently.
