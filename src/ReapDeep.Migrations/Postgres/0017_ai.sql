ALTER TABLE projects ADD COLUMN ai_enabled BOOLEAN NOT NULL DEFAULT false;

CREATE TABLE ai_usage (
    project_id  BIGINT  NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    day         DATE    NOT NULL,
    calls       INT     NOT NULL DEFAULT 0,
    PRIMARY KEY (project_id, day)
);
