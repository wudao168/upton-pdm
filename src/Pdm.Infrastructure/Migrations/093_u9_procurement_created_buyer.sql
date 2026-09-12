ALTER TABLE u9_procurement_snapshot
    ADD COLUMN source_created_at DATETIME(6) NULL,
    ADD COLUMN buyer_name VARCHAR(255) NULL;
