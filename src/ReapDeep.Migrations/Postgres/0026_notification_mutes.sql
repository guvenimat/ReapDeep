CREATE TABLE notification_mutes (
    user_id     BIGINT  NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    project_id  BIGINT  NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    kind        TEXT    NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, project_id, kind)
);
