# ADR 0004: PostgreSQL is the source of truth for persons

Status: accepted

## Context

Identity resolution (`$identify`, `$set`) needs transactions and locks: two events for the same visitor can arrive at the same time, and two identified persons must never be merged. StarRocks has upserts but no multi-row transactions.

## Decision

The person worker consumes only `$identify` and `$set` from Kafka and applies the merge rules in PostgreSQL, one transaction per event with advisory locks on the distinct ids involved. Changed rows are written to `person_outbox` in the same transaction and published to StarRocks by a separate loop; StarRocks applies them with `merge_condition = version`, so an older message never overwrites a newer row.

## Consequences

- No lost updates when the worker dies between writing PostgreSQL and publishing.
- Events are never rewritten; queries resolve the person at read time.
- A deletion request removes the person in PostgreSQL first and lets the outbox carry the tombstones to StarRocks.
