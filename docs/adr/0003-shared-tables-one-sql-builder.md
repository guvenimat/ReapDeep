# ADR 0003: Shared multi-tenant tables behind one SQL builder

Status: accepted

## Context

ReapDeep serves many projects from one cluster. A table or database per customer is simple to isolate but does not scale to many small tenants and makes schema changes slow. Shared tables are efficient, but a single query without a project filter leaks another customer's data.

## Decision

All projects share the same tables, with `project_id` first in every sort and distribution key. Every analytics statement against StarRocks is produced by `SqlBuilder` in `ReapDeep.Analytics` (and `CohortSql`, which reuses its parameters). Its constructor takes the project id and a UTC time range and always adds both filters; there is no code path that builds a query without them. Values and JSON paths are always parameters; property names are validated against a strict pattern.

The Query API connects to StarRocks as a SELECT-only user.

## Consequences

- Tenant isolation is a property of one class, which is unit tested and covered by contract tests.
- A new analysis cannot be written as hand-made SQL in an endpoint; it has to go through the builder.
- Per-project quotas (events, alerts, cohorts) protect shared capacity from one large tenant.
