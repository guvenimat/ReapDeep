CREATE TABLE IF NOT EXISTS feature_flags (
    id                 BIGSERIAL   PRIMARY KEY,
    project_id         BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    key                TEXT        NOT NULL,
    name               TEXT        NOT NULL DEFAULT '',
    description        TEXT        NOT NULL DEFAULT '',
    enabled            BOOLEAN     NOT NULL DEFAULT false,
    rollout_percentage INT         NOT NULL DEFAULT 100 CHECK (rollout_percentage BETWEEN 0 AND 100),
    variants           JSONB       NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(variants) = 'array'),
    conditions         JSONB       NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(conditions) = 'array'),
    salt               TEXT        NOT NULL,
    created_by         BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    updated_by         BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (project_id, key)
);
CREATE INDEX IF NOT EXISTS feature_flags_updated ON feature_flags (updated_at);
