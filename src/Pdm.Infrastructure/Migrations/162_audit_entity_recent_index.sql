CREATE INDEX ix_audit_entity_recent ON audit_entry(entity_type,entity_id,occurred_at,id);
