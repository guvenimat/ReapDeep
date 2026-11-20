ALTER TABLE insights DROP CONSTRAINT IF EXISTS insights_kind_check;

ALTER TABLE insights ADD CONSTRAINT insights_kind_check
    CHECK (kind IN ('trends', 'funnels', 'retention', 'paths', 'rfm', 'lifecycle', 'stickiness', 'web'));
