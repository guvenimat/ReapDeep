CREATE TABLE organizations (
    id          BIGSERIAL PRIMARY KEY,
    name        TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE users (
    id             BIGSERIAL PRIMARY KEY,
    email          TEXT        NOT NULL UNIQUE,
    name           TEXT        NOT NULL,
    password_hash  TEXT        NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE organization_members (
    organization_id  BIGINT      NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    user_id          BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role             TEXT        NOT NULL CHECK (role IN ('owner', 'admin', 'member')),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (organization_id, user_id)
);

CREATE TABLE sessions (
    token_hash  TEXT        PRIMARY KEY,
    user_id     BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL
);
CREATE INDEX sessions_user ON sessions (user_id);

CREATE TABLE projects (
    id               BIGSERIAL PRIMARY KEY,
    organization_id  BIGINT      NOT NULL REFERENCES organizations(id),
    name             TEXT        NOT NULL,
    public_key       TEXT        NOT NULL UNIQUE,
    timezone         TEXT        NOT NULL DEFAULT 'Europe/Istanbul',
    recommendation_secret TEXT,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE api_keys (
    id            BIGSERIAL PRIMARY KEY,
    project_id    BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name          TEXT        NOT NULL,
    key_prefix    TEXT        NOT NULL,
    key_hash      TEXT        NOT NULL UNIQUE,
    created_by    BIGINT      REFERENCES users(id),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_used_at  TIMESTAMPTZ,
    revoked_at    TIMESTAMPTZ,
    scope         TEXT        NOT NULL DEFAULT 'read' CHECK (scope IN ('read', 'catalog_write'))
);

CREATE TABLE event_definitions (
    project_id    BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name          TEXT        NOT NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, name)
);

CREATE TABLE property_definitions (
    project_id     BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    scope          TEXT        NOT NULL CHECK (scope IN ('event', 'person')),
    name           TEXT        NOT NULL,
    property_type  TEXT        CHECK (property_type IN ('string', 'number', 'boolean', 'datetime')),
    column_name    TEXT,
    last_seen_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, scope, name)
);

CREATE TABLE deletion_requests (
    id            BIGSERIAL PRIMARY KEY,
    project_id    BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    target_type   TEXT        NOT NULL CHECK (target_type IN ('person', 'distinct_id')),
    target_id     TEXT        NOT NULL,
    requested_by  BIGINT      REFERENCES users(id),
    requested_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at  TIMESTAMPTZ
);
CREATE INDEX deletion_requests_pending ON deletion_requests (requested_at) WHERE processed_at IS NULL;

CREATE TABLE persons (
    id             UUID        PRIMARY KEY,
    project_id     BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    properties     JSONB       NOT NULL DEFAULT '{}',
    is_identified  BOOLEAN     NOT NULL DEFAULT false,
    is_deleted     BOOLEAN     NOT NULL DEFAULT false,
    version        BIGINT      NOT NULL DEFAULT 0,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE person_distinct_ids (
    project_id   BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    distinct_id  TEXT        NOT NULL,
    person_id    UUID        NOT NULL REFERENCES persons(id),
    version      BIGINT      NOT NULL DEFAULT 0,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, distinct_id)
);
CREATE INDEX person_distinct_ids_person ON person_distinct_ids (person_id);

CREATE TABLE person_outbox (
    id          BIGSERIAL   PRIMARY KEY,
    topic       TEXT        NOT NULL,
    key         TEXT        NOT NULL,
    payload     JSONB       NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE insights (
    id          BIGSERIAL   PRIMARY KEY,
    project_id  BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name        TEXT        NOT NULL,
    kind        TEXT        NOT NULL CHECK (kind IN ('trends', 'funnels', 'retention', 'paths', 'rfm', 'lifecycle')),
    query       JSONB       NOT NULL,
    created_by  BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX insights_project ON insights (project_id, updated_at DESC);

CREATE TABLE dashboards (
    id          BIGSERIAL   PRIMARY KEY,
    project_id  BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name        TEXT        NOT NULL,
    description TEXT        NOT NULL DEFAULT '',
    created_by  BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX dashboards_project ON dashboards (project_id, updated_at DESC);

CREATE TABLE dashboard_items (
    dashboard_id  BIGINT NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    insight_id    BIGINT NOT NULL REFERENCES insights(id) ON DELETE CASCADE,
    position      INT    NOT NULL,
    PRIMARY KEY (dashboard_id, insight_id)
);

CREATE TABLE cohorts (
    id                       BIGSERIAL   PRIMARY KEY,
    project_id               BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name                     TEXT        NOT NULL,
    description              TEXT        NOT NULL DEFAULT '',
    kind                     TEXT        NOT NULL CHECK (kind IN ('dynamic', 'static')),
    definition               JSONB       NOT NULL DEFAULT '{}',
    status                   TEXT        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'calculating', 'ready', 'error')),
    active_version           BIGINT,
    member_count             BIGINT,
    calculated_at            TIMESTAMPTZ,
    calculation_ms           INT,
    last_error               TEXT,
    refresh_interval_seconds INT         NOT NULL DEFAULT 3600,
    created_by               BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at               TIMESTAMPTZ
);
CREATE INDEX cohorts_project ON cohorts (project_id, updated_at DESC);
CREATE INDEX cohorts_due ON cohorts (status, calculated_at);

CREATE TABLE cohort_static_members (
    cohort_id    BIGINT       NOT NULL REFERENCES cohorts(id) ON DELETE CASCADE,
    project_id   BIGINT       NOT NULL,
    distinct_id  VARCHAR(200) NOT NULL,
    PRIMARY KEY (cohort_id, distinct_id)
);
CREATE INDEX cohort_static_members_distinct_id ON cohort_static_members (project_id, distinct_id);

CREATE TABLE cohort_retired_versions (
    cohort_id   BIGINT      NOT NULL REFERENCES cohorts(id) ON DELETE CASCADE,
    project_id  BIGINT      NOT NULL,
    version     BIGINT      NOT NULL,
    retired_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (cohort_id, version)
);

CREATE TABLE items (
    project_id  BIGINT        NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    item_id     VARCHAR(200)  NOT NULL,
    title       TEXT          NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    category    TEXT          NOT NULL DEFAULT '',
    price       NUMERIC(12, 2),
    currency    VARCHAR(10)   NOT NULL DEFAULT 'TRY',
    in_stock    BOOLEAN       NOT NULL DEFAULT true,
    url         TEXT          NOT NULL DEFAULT '',
    image_url   TEXT          NOT NULL DEFAULT '',
    attributes  JSONB         NOT NULL DEFAULT '{}',
    updated_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, item_id)
);
CREATE INDEX items_stock_category ON items (project_id, in_stock, category);
CREATE INDEX items_updated ON items (project_id, updated_at DESC);

CREATE TABLE item_recommendations (
    project_id         BIGINT        NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    model              TEXT          NOT NULL CHECK (model IN ('popular', 'trending', 'co_viewed', 'bought_together')),
    source_item_id     VARCHAR(200)  NOT NULL DEFAULT '',
    recommended_items  JSONB         NOT NULL DEFAULT '[]',
    calculated_at      TIMESTAMPTZ   NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, model, source_item_id)
);

CREATE TABLE person_item_history (
    project_id     BIGINT        NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    person_key     VARCHAR(200)  NOT NULL,
    items          JSONB         NOT NULL DEFAULT '[]',
    calculated_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, person_key)
);

CREATE TABLE recommendation_runs (
    project_id      BIGINT       NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    model           TEXT         NOT NULL,
    calculated_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    calculation_ms  INT          NOT NULL,
    source_items    INT          NOT NULL,
    PRIMARY KEY (project_id, model)
);

CREATE TABLE alerts (
    id               BIGSERIAL        PRIMARY KEY,
    project_id       BIGINT           NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    insight_id       BIGINT           NOT NULL REFERENCES insights(id) ON DELETE CASCADE,
    name             TEXT             NOT NULL,
    condition        TEXT             NOT NULL CHECK (condition IN ('above', 'below', 'increase', 'decrease')),
    threshold        DOUBLE PRECISION NOT NULL,
    window_unit      TEXT             NOT NULL CHECK (window_unit IN ('hour', 'day', 'week')),
    webhook_url      TEXT             NOT NULL,
    secret           TEXT             NOT NULL,
    enabled          BOOLEAN          NOT NULL DEFAULT true,
    state            TEXT             NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'ok', 'firing', 'error')),
    last_value       DOUBLE PRECISION,
    previous_value   DOUBLE PRECISION,
    last_error       TEXT,
    last_checked_at  TIMESTAMPTZ,
    next_check_at    TIMESTAMPTZ      NOT NULL DEFAULT now(),
    created_by       BIGINT           REFERENCES users(id) ON DELETE SET NULL,
    created_at       TIMESTAMPTZ      NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ      NOT NULL DEFAULT now()
);
CREATE INDEX alerts_project ON alerts (project_id, created_at DESC);
CREATE INDEX alerts_due ON alerts (next_check_at) WHERE enabled;

CREATE TABLE alert_events (
    id               BIGSERIAL        PRIMARY KEY,
    alert_id         BIGINT           NOT NULL REFERENCES alerts(id) ON DELETE CASCADE,
    project_id       BIGINT           NOT NULL,
    kind             TEXT             NOT NULL CHECK (kind IN ('fired', 'resolved', 'error')),
    value            DOUBLE PRECISION,
    previous_value   DOUBLE PRECISION,
    message          TEXT             NOT NULL,
    response_status  INT,
    delivery_error   TEXT,
    created_at       TIMESTAMPTZ      NOT NULL DEFAULT now()
);
CREATE INDEX alert_events_alert ON alert_events (alert_id, created_at DESC);

INSERT INTO organizations (name) VALUES ('Local Org');
INSERT INTO projects (organization_id, name, public_key) VALUES (1, 'Local Project', 'pk_dev_local');
