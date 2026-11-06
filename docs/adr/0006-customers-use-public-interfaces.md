# ADR 0006: Customers integrate only through public interfaces

Status: accepted

## Context

Customers such as online stores integrate with ReapDeep from their own systems. During development a separate sample store, [ReapSeek](https://github.com/guvenimat/ReapSeek), is used to exercise these integrations; ReapDeep does not depend on it. Letting a customer read Kafka, StarRocks or PostgreSQL directly would be quicker, but would make ReapDeep's storage a public contract and hide gaps in the product.

## Decision

A customer uses only what any customer could use:

| Need | Interface | Credential |
|---|---|---|
| Browser events | JS SDK, `POST /capture` | public key |
| Server events (purchases, refunds) | Ingestion API with stable `event_id`s | public key |
| Product catalog | `PUT /api/v1/items`, `DELETE /items/{id}` | `ck_` catalog key |
| Recommendations | Recommender | public key, signed request for `for_you` |
| Cohorts, person cohort lookup, RFM segments, person profiles | Query API | `rk_` read key |
| Alerts | signed webhooks (`X-ReapDeep-Signature`) | per-alert secret |

When an integration needs something that does not exist, it is built in ReapDeep as a general feature for every customer, never as a special case for one. Customers are expected to send pseudonymous ids rather than personal data.

## Consequences

- Storage can change without breaking customers; the API is the contract, covered by contract tests.
- Building the sample store surfaced real product gaps (catalog keys, signed personal recommendations, cohort member listing, alert quotas) that are now general features.
