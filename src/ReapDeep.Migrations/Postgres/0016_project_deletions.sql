CREATE TABLE project_deletions (
    id               BIGSERIAL    PRIMARY KEY,
    project_id       BIGINT       NOT NULL,
    organization_id  BIGINT       NOT NULL,
    name             TEXT         NOT NULL,
    requested_by     BIGINT       REFERENCES users(id) ON DELETE SET NULL,
    requested_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    completed_at     TIMESTAMPTZ,
    last_error       TEXT
);

CREATE INDEX project_deletions_pending ON project_deletions (requested_at) WHERE completed_at IS NULL;
