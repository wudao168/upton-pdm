CREATE INDEX ix_audit_action_entity_time ON audit_entry (action_name, entity_id, occurred_at);
