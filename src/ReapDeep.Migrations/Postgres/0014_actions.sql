CREATE TABLE actions (
    id           BIGSERIAL    PRIMARY KEY,
    project_id   BIGINT       NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name         TEXT         NOT NULL CHECK (char_length(name) BETWEEN 1 AND 100),
    description  TEXT         NOT NULL DEFAULT '',
    events       TEXT[]       NOT NULL CHECK (cardinality(events) BETWEEN 1 AND 20),
    created_by   BIGINT       REFERENCES users(id) ON DELETE SET NULL,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    UNIQUE (project_id, name)
);
