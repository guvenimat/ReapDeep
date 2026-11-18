CREATE TABLE organization_ai (
    organization_id    BIGINT       PRIMARY KEY REFERENCES organizations(id) ON DELETE CASCADE,
    platform_enabled   BOOLEAN      NOT NULL DEFAULT false,
    platform_features  TEXT[]       NOT NULL DEFAULT '{}',
    customer_enabled   BOOLEAN      NOT NULL DEFAULT true,
    customer_features  TEXT[]       NOT NULL DEFAULT '{experiment_explain,insight_explain,query,digest}',
    updated_at         TIMESTAMPTZ  NOT NULL DEFAULT now()
);

INSERT INTO organization_ai (organization_id, platform_enabled, platform_features)
SELECT DISTINCT organization_id, true, '{experiment_explain,insight_explain,query,digest}'::text[] FROM projects WHERE ai_enabled;

ALTER TABLE projects DROP COLUMN ai_enabled;
