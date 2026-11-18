CREATE TABLE notifications (
    id          BIGSERIAL    PRIMARY KEY,
    project_id  BIGINT       NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    kind        TEXT         NOT NULL,
    title       TEXT         NOT NULL,
    body        TEXT         NOT NULL DEFAULT '',
    link        TEXT         NOT NULL DEFAULT '',
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX notifications_project ON notifications (project_id, id DESC);

CREATE TABLE notification_reads (
    user_id       BIGINT  NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    project_id    BIGINT  NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    last_read_id  BIGINT  NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, project_id)
);
