# ADR 0001: StarRocks as the event store

Status: accepted

## Context

Trend, funnel, retention and path queries must answer over hundreds of millions of events in about a second. Persons change after the fact: an anonymous visitor is merged into an identified user, and person properties are updated. Events must become queryable within seconds of ingestion.

## Decision

Events, the distinct-ID-to-person mapping and persons live in StarRocks. `events` is an append-only Duplicate Key table; `person_distinct_ids` and `persons` are Primary Key tables with real upserts ordered by a `version` column. `events` and `person_distinct_ids` share a colocate group hashed by `(project_id, distinct_id)`, so the person join runs on each node without a shuffle. StarRocks reads Kafka directly with Routine Load.

ClickHouse was the main alternative. The same person model there needs ReplacingMergeTree with `FINAL` or argMax rewrites, and a separate consumer service for Kafka.

## Consequences

- Identity merges never rewrite events; the person is resolved at read time with a `LEFT JOIN` ([architecture, section 5](../architecture.md#5-identity-resolution)).
- Queries over 100M events meet the target on one machine ([section 9](../architecture.md#9-performance-results)).
- The colocate group fixes the bucket count for good, so the production count (32) was chosen from measured row sizes.
- Routine Load is sensitive to malformed messages and offset rewinds; a watchdog job restarts paused loads and offsets are never rewound by hand.
