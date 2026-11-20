USE analytics;

CREATE TABLE IF NOT EXISTS cohort_members_history (
    project_id    BIGINT        NOT NULL,
    cohort_id     BIGINT        NOT NULL,
    snapshot_day  DATE          NOT NULL,
    distinct_id   VARCHAR(200)  NOT NULL
)
PRIMARY KEY(project_id, cohort_id, snapshot_day, distinct_id)
DISTRIBUTED BY HASH(project_id, distinct_id) BUCKETS 8
PROPERTIES (
    "replication_num" = "1",
    "colocate_with" = "events_pdi",
    "compression" = "ZSTD"
);
