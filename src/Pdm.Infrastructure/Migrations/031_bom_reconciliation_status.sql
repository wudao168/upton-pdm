ALTER TABLE bom_item
    ADD COLUMN reconciliation_status VARCHAR(40) NULL AFTER is_manually_excluded,
    ADD COLUMN reconciliation_note VARCHAR(500) NULL AFTER reconciliation_status,
    ADD COLUMN reconciliation_updated_by VARCHAR(100) NULL AFTER reconciliation_note,
    ADD COLUMN reconciliation_updated_at DATETIME(6) NULL AFTER reconciliation_updated_by;
