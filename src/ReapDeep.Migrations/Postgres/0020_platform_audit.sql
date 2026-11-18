CREATE TABLE platform_audit (
    id                 BIGSERIAL    PRIMARY KEY,
    actor_email        TEXT         NOT NULL,
    organization_id    BIGINT       REFERENCES organizations(id) ON DELETE SET NULL,
    organization_name  TEXT         NOT NULL,
    action             TEXT         NOT NULL,
    before_state       JSONB        NOT NULL,
    after_state        JSONB        NOT NULL,
    created_at         TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX platform_audit_created ON platform_audit (created_at DESC);
