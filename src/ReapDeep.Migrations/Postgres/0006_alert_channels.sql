ALTER TABLE alerts ADD COLUMN IF NOT EXISTS channel  TEXT NOT NULL DEFAULT 'webhook';
ALTER TABLE alerts ADD COLUMN IF NOT EXISTS email_to TEXT[];
ALTER TABLE alerts ALTER COLUMN webhook_url DROP NOT NULL;
ALTER TABLE alerts DROP CONSTRAINT IF EXISTS alerts_channel_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_channel_check CHECK (
    (channel IN ('webhook', 'slack') AND webhook_url IS NOT NULL)
    OR (channel = 'email' AND email_to IS NOT NULL AND cardinality(email_to) BETWEEN 1 AND 10));

ALTER TABLE alert_events ADD COLUMN IF NOT EXISTS channel TEXT NOT NULL DEFAULT 'webhook';
