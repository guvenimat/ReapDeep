ALTER TABLE alert_events
    ADD COLUMN IF NOT EXISTS payload         TEXT,
    ADD COLUMN IF NOT EXISTS delivery_status TEXT        NOT NULL DEFAULT 'none',
    ADD COLUMN IF NOT EXISTS attempts        INT         NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS next_attempt_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS delivered_at    TIMESTAMPTZ;

UPDATE alert_events SET delivery_status = 'failed', attempts = 1
WHERE kind IN ('fired', 'resolved') AND delivery_error IS NOT NULL AND delivery_status = 'none';
UPDATE alert_events SET delivery_status = 'delivered', attempts = 1, delivered_at = created_at
WHERE kind IN ('fired', 'resolved') AND delivery_error IS NULL AND delivery_status = 'none';

ALTER TABLE alert_events DROP CONSTRAINT IF EXISTS alert_events_delivery_status_check;
ALTER TABLE alert_events ADD CONSTRAINT alert_events_delivery_status_check
    CHECK (delivery_status IN ('none', 'pending', 'delivered', 'failed'));

CREATE INDEX IF NOT EXISTS alert_events_retry ON alert_events (next_attempt_at) WHERE delivery_status = 'pending';
