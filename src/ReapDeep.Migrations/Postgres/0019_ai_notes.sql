CREATE TABLE ai_notes (
    id           BIGSERIAL    PRIMARY KEY,
    project_id   BIGINT       NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    trigger      TEXT         NOT NULL CHECK (trigger IN ('manual', 'schedule')),
    period_from  DATE         NOT NULL,
    period_to    DATE         NOT NULL,
    answer       JSONB        NOT NULL,
    emailed_to   TEXT[]       NOT NULL DEFAULT '{}',
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX ai_notes_project ON ai_notes (project_id, created_at DESC);

CREATE TABLE ai_digest_schedules (
    project_id   BIGINT       PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
    frequency    TEXT         NOT NULL CHECK (frequency IN ('daily', 'weekly', 'monthly')),
    weekday      SMALLINT     NOT NULL DEFAULT 1 CHECK (weekday BETWEEN 1 AND 7),
    month_day    SMALLINT     NOT NULL DEFAULT 1 CHECK (month_day BETWEEN 1 AND 28),
    hour         SMALLINT     NOT NULL DEFAULT 9 CHECK (hour BETWEEN 0 AND 23),
    email_to     TEXT[]       NOT NULL DEFAULT '{}',
    next_run_at  TIMESTAMPTZ  NOT NULL,
    last_run_at  TIMESTAMPTZ,
    last_error   TEXT,
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX ai_digest_schedules_due ON ai_digest_schedules (next_run_at);
