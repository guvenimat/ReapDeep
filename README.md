# ReapDeep

Multi-tenant, high-performance web product analytics platform (in the spirit of PostHog / Mixpanel).

```
JS SDK ──► Ingestion API ──► Kafka ──► StarRocks ◄── Query API ◄── Dashboard
                               │                        │
                               └──► Person worker ──► PostgreSQL
```

| Component | Path | Stack |
|---|---|---|
| Ingestion API | `src/ReapDeep.Ingestion` | .NET 10 Minimal API, Native AOT, Confluent.Kafka |
| Person worker (identity resolution) | `src/ReapDeep.PersonWorker` | .NET 10 Worker, Native AOT, transactional outbox |
| Query API | `src/ReapDeep.QueryApi` | .NET 10 Minimal API, Native AOT, MySqlConnector + Npgsql |
| JS SDK | `sdk/js` | TypeScript, esbuild, ~2.4 KB gzip |
| Dashboard | `dashboard` | React 19, TypeScript, Vite, hand-written SVG charts |
| Local stack | `deploy/local` | Docker Compose: Kafka 4.1 (KRaft), StarRocks 4.0, PostgreSQL 17 |

## Getting started

Requirements: Docker with at least **8 GB RAM**, .NET 10 SDK and Node.js 22+ for local development.

```powershell
cd deploy/local
.\run-local.ps1                                  # creates .env, starts the stack, runs a smoke test
docker compose --profile app up -d --build      # ingestion, person worker, query API, dashboard
```

On Linux/macOS: `cp .env.example .env` (or let `./smoke-test.sh` generate it), then `docker compose up -d`.

`run-local.ps1` creates `deploy/local/.env` with a random PostgreSQL password and a random read API key (`READ_API_KEY`). The file is git-ignored. All ports are bound to `127.0.0.1` only.

| Service | Address |
|---|---|
| Dashboard | http://localhost:3000 (enter `READ_API_KEY` from `.env`) |
| Ingestion API | `POST http://localhost:8000/capture` (public project key `pk_dev_local`) |
| Query API | http://localhost:8001 (`Authorization: Bearer <READ_API_KEY>`) |
| StarRocks (MySQL protocol) | `localhost:9030`, user `root` |
| PostgreSQL | `localhost:5432`, user `reapdeep`, password in `.env` |
| Kafka | `localhost:9092` |

To run a .NET service outside Docker, pass the connection string through the environment, e.g. `ConnectionStrings__Postgres="Host=localhost;Database=reapdeep;Username=reapdeep;Password=<from .env>"`.

## Tests

```powershell
cd deploy/local
.\identity-test.ps1     # identity resolution scenarios end to end
.\query-test.ps1        # Query API: trends, funnels, events, persons, EXPLAIN, latency
.\dashboard-test.ps1    # headless Chrome against the dashboard (Chrome DevTools Protocol)

cd ../../sdk/js
npm test                # SDK unit tests
.\demo\e2e.ps1          # real browser -> ingestion -> Kafka -> StarRocks
```

## Useful commands

```bash
docker compose exec starrocks mysql -h127.0.0.1 -P9030 -uroot
docker compose exec starrocks mysql -h127.0.0.1 -P9030 -uroot -e "SHOW ROUTINE LOAD FROM analytics\G"
docker compose --profile app down -v    # reset everything, including data
```
