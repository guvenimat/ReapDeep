USE analytics;

CREATE TABLE IF NOT EXISTS cohort_members (
    project_id   BIGINT        NOT NULL,
    cohort_id    BIGINT        NOT NULL,
    version      BIGINT        NOT NULL,
    distinct_id  VARCHAR(200)  NOT NULL
)
PRIMARY KEY(project_id, cohort_id, version, distinct_id)
DISTRIBUTED BY HASH(project_id, distinct_id) BUCKETS 8
PROPERTIES (
    "replication_num" = "1",
    "colocate_with" = "events_pdi",
    "compression" = "ZSTD"
);
