ALTER TABLE api_keys DROP CONSTRAINT api_keys_scope_check;

ALTER TABLE api_keys ADD CONSTRAINT api_keys_scope_check CHECK (scope IN ('read', 'catalog_write', 'server'));

CREATE INDEX api_keys_server ON api_keys (key_hash) WHERE scope = 'server' AND revoked_at IS NULL;
