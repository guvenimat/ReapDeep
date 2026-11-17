CREATE TABLE IF NOT EXISTS experiments (
    id              BIGSERIAL   PRIMARY KEY,
    project_id      BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    flag_id         BIGINT      NOT NULL REFERENCES feature_flags(id),
    name            TEXT        NOT NULL,
    description     TEXT        NOT NULL DEFAULT '',
    status          TEXT        NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'running', 'stopped')),
    metric          JSONB       NOT NULL CHECK (jsonb_typeof(metric) = 'object'),
    control_variant TEXT,
    start_date      TIMESTAMPTZ,
    end_date        TIMESTAMPTZ,
    created_by      BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    updated_by      BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (end_date IS NULL OR start_date IS NULL OR end_date > start_date)
);
CREATE INDEX IF NOT EXISTS experiments_project ON experiments (project_id);
CREATE INDEX IF NOT EXISTS experiments_flag ON experiments (flag_id, status);
