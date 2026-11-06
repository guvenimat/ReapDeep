ALTER TABLE projects
    ADD COLUMN anonymize_ip BOOLEAN NOT NULL DEFAULT true,
    ADD COLUMN ingest_events_per_second INTEGER
        CHECK (ingest_events_per_second IS NULL OR ingest_events_per_second = 0 OR ingest_events_per_second >= 1000);
