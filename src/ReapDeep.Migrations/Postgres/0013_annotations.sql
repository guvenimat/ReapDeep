CREATE TABLE annotations (
    id          BIGSERIAL    PRIMARY KEY,
    project_id  BIGINT       NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    day         DATE         NOT NULL,
    content     TEXT         NOT NULL CHECK (char_length(content) BETWEEN 1 AND 300),
    created_by  BIGINT       REFERENCES users(id) ON DELETE SET NULL,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX annotations_project_day ON annotations (project_id, day);
