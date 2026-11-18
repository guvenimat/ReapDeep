CREATE TABLE flag_cohort_members (
    cohort_id    BIGINT        NOT NULL REFERENCES cohorts(id) ON DELETE CASCADE,
    distinct_id  VARCHAR(200)  NOT NULL,
    PRIMARY KEY (cohort_id, distinct_id)
);

ALTER TABLE cohorts
    ADD COLUMN flag_synced_version BIGINT,
    ADD COLUMN flag_synced_at      TIMESTAMPTZ,
    ADD COLUMN flag_sync_error     TEXT;
