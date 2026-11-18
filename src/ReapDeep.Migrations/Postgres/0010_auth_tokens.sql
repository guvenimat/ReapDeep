ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;

CREATE TABLE auth_tokens (
    token_hash  TEXT        PRIMARY KEY,
    user_id     BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    purpose     TEXT        NOT NULL CHECK (purpose IN ('invite', 'reset')),
    expires_at  TIMESTAMPTZ NOT NULL,
    used_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX auth_tokens_user ON auth_tokens (user_id, created_at);
