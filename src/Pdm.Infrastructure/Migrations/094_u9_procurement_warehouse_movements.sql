ALTER TABLE u9_procurement_snapshot
    ADD COLUMN movement_date DATETIME(6) NULL,
    ADD COLUMN movement_quantity DECIMAL(24,9) NULL,
    ADD COLUMN movement_unit VARCHAR(100) NULL;
