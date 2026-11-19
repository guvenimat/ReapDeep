ALTER TABLE experiments ADD COLUMN secondary_metrics JSONB NOT NULL DEFAULT '[]'
    CHECK (jsonb_typeof(secondary_metrics) = 'array');
