# ADR 0006: Customers integrate only through public interfaces

Status: accepted

## Context

[ReapSeek](https://github.com/guvenimat/ReapSeek), a multi-seller marketplace, is the reference customer of ReapDeep and is developed alongside it. Letting it read Kafka, StarRocks or PostgreSQL directly would be quicker, but would make ReapDeep's storage a public contract and hide gaps in the product.

## Decision

A customer uses only what any customer could use:

| Need | Interface | Credential |
|---|---|---|
| Browser events | JS SDK, `POST /capture` | public key |
| Server events (purchases, refunds) | Ingestion API with stable `event_id`s | public key |
| Product catalog | `PUT /api/v1/items`, `DELETE /items/{id}` | `ck_` catalog key |
| Recommendations | Recommender | public key, signed request for `for_you` |
| Cohorts, RFM segments, person profiles | Query API | `rk_` read key |
| Alerts | signed webhooks (`X-ReapDeep-Signature`) | per-alert secret |

When ReapSeek needs something that does not exist, it is built in ReapDeep as a feature for every customer. Only pseudonymous ids (`customer-{id}`) are sent; no personal data.

## Consequences

- Storage can change without breaking customers; the API is the contract, covered by contract tests.
- Building the marketplace surfaced real product gaps (catalog keys, signed personal recommendations, cohort member listing, alert quotas) that are now general features.
