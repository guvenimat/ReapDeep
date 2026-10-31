CREATE DATABASE IF NOT EXISTS analytics;
USE analytics;

CREATE TABLE IF NOT EXISTS events (
    project_id        BIGINT         NOT NULL,
    event             VARCHAR(200)   NOT NULL,
    timestamp         DATETIME       NOT NULL,
    distinct_id       VARCHAR(200)   NOT NULL,
    event_id          VARCHAR(64)    NOT NULL,
    session_id        VARCHAR(64)    NULL,
    ip                VARCHAR(45)    NULL,
    current_url       VARCHAR(4096)  NULL,
    host              VARCHAR(255)   NULL,
    pathname          VARCHAR(2048)  NULL,
    referrer          VARCHAR(4096)  NULL,
    referring_domain  VARCHAR(255)   NULL,
    utm_source        VARCHAR(255)   NULL,
    utm_medium        VARCHAR(255)   NULL,
    utm_campaign      VARCHAR(255)   NULL,
    utm_term          VARCHAR(255)   NULL,
    utm_content       VARCHAR(255)   NULL,
    browser           VARCHAR(64)    NULL,
    browser_version   VARCHAR(32)    NULL,
    os                VARCHAR(64)    NULL,
    os_version        VARCHAR(32)    NULL,
    device_type       VARCHAR(16)    NULL,
    screen_width      SMALLINT       NULL,
    screen_height     SMALLINT       NULL,
    lib               VARCHAR(32)    NULL,
    lib_version       VARCHAR(32)    NULL,
    item_id           VARCHAR(200)   NULL,
    properties        JSON           NULL,
    inserted_at       DATETIME       NULL
)
DUPLICATE KEY(project_id, event, timestamp)
PARTITION BY date_trunc('day', timestamp)
DISTRIBUTED BY HASH(project_id, distinct_id) BUCKETS 8
PROPERTIES (
    "replication_num" = "1",
    "colocate_with" = "events_pdi",
    "partition_live_number" = "365",
    "bloom_filter_columns" = "distinct_id, session_id",
    "compression" = "ZSTD",
    "flat_json.enable" = "true"
);

CREATE TABLE IF NOT EXISTS person_distinct_ids (
    project_id   BIGINT        NOT NULL,
    distinct_id  VARCHAR(200)  NOT NULL,
    person_id    VARCHAR(36)   NOT NULL,
    version      BIGINT        NOT NULL DEFAULT "0",
    is_deleted   BOOLEAN       NOT NULL DEFAULT "false",
    updated_at   DATETIME      NULL
)
PRIMARY KEY(project_id, distinct_id)
DISTRIBUTED BY HASH(project_id, distinct_id) BUCKETS 8
PROPERTIES (
    "replication_num" = "1",
    "colocate_with" = "events_pdi"
);

CREATE TABLE IF NOT EXISTS persons (
    project_id   BIGINT        NOT NULL,
    person_id    VARCHAR(36)   NOT NULL,
    properties   JSON          NULL,
    is_identified BOOLEAN      NOT NULL DEFAULT "false",
    created_at   DATETIME      NOT NULL,
    version      BIGINT        NOT NULL DEFAULT "0",
    is_deleted   BOOLEAN       NOT NULL DEFAULT "false",
    updated_at   DATETIME      NULL
)
PRIMARY KEY(project_id, person_id)
DISTRIBUTED BY HASH(project_id, person_id) BUCKETS 8
PROPERTIES (
    "replication_num" = "1",
    "compression" = "ZSTD"
);

CREATE ROUTINE LOAD analytics.events_from_kafka ON events
COLUMNS (project_id, event, timestamp, distinct_id, event_id, session_id, ip,
         current_url, host, pathname, referrer, referring_domain,
         utm_source, utm_medium, utm_campaign, utm_term, utm_content,
         browser, browser_version, os, os_version, device_type, screen_width, screen_height,
         lib, lib_version, item_id, properties, inserted_at)
PROPERTIES (
    "format" = "json",
    "jsonpaths" = "[\"$.project_id\",\"$.event\",\"$.timestamp\",\"$.distinct_id\",\"$.event_id\",\"$.session_id\",\"$.ip\",\"$.current_url\",\"$.host\",\"$.pathname\",\"$.referrer\",\"$.referring_domain\",\"$.utm_source\",\"$.utm_medium\",\"$.utm_campaign\",\"$.utm_term\",\"$.utm_content\",\"$.browser\",\"$.browser_version\",\"$.os\",\"$.os_version\",\"$.device_type\",\"$.screen_width\",\"$.screen_height\",\"$.lib\",\"$.lib_version\",\"$.item_id\",\"$.properties\",\"$.inserted_at\"]",
    "desired_concurrent_number" = "3",
    "max_batch_interval" = "5",
    "max_error_number" = "1000"
)
FROM KAFKA (
    "kafka_broker_list" = "kafka:29092",
    "kafka_topic" = "events",
    "property.kafka_default_offsets" = "OFFSET_BEGINNING"
);

CREATE ROUTINE LOAD analytics.persons_from_kafka ON persons
COLUMNS (project_id, person_id, properties, is_identified, created_at, version, is_deleted, updated_at)
PROPERTIES (
    "format" = "json",
    "jsonpaths" = "[\"$.project_id\",\"$.person_id\",\"$.properties\",\"$.is_identified\",\"$.created_at\",\"$.version\",\"$.is_deleted\",\"$.updated_at\"]",
    "merge_condition" = "version",
    "desired_concurrent_number" = "1",
    "max_batch_interval" = "5",
    "max_error_number" = "1000"
)
FROM KAFKA (
    "kafka_broker_list" = "kafka:29092",
    "kafka_topic" = "person_updates",
    "property.kafka_default_offsets" = "OFFSET_BEGINNING"
);

CREATE ROUTINE LOAD analytics.person_distinct_ids_from_kafka ON person_distinct_ids
COLUMNS (project_id, distinct_id, person_id, version, is_deleted, updated_at)
PROPERTIES (
    "format" = "json",
    "jsonpaths" = "[\"$.project_id\",\"$.distinct_id\",\"$.person_id\",\"$.version\",\"$.is_deleted\",\"$.updated_at\"]",
    "merge_condition" = "version",
    "desired_concurrent_number" = "1",
    "max_batch_interval" = "5",
    "max_error_number" = "1000"
)
FROM KAFKA (
    "kafka_broker_list" = "kafka:29092",
    "kafka_topic" = "person_distinct_id_updates",
    "property.kafka_default_offsets" = "OFFSET_BEGINNING"
);
