ALTER TABLE bom_item
    ADD COLUMN deleted_at DATETIME(6) NULL AFTER reconciliation_updated_at,
    ADD COLUMN deleted_by VARCHAR(100) NULL AFTER deleted_at,
    ADD COLUMN delete_reason VARCHAR(500) NULL AFTER deleted_by;

CREATE INDEX ix_bom_item_recycle_bin ON bom_item(project_id, deleted_at);
