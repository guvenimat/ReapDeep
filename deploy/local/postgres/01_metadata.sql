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
    revoked_at    TIMESTAMPTZ
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
    kind        TEXT        NOT NULL CHECK (kind IN ('trends', 'funnels')),
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

INSERT INTO organizations (name) VALUES ('Local Org');
INSERT INTO projects (organization_id, name, public_key) VALUES (1, 'Local Project', 'pk_dev_local');
