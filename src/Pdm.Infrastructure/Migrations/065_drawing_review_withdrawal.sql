ALTER TABLE drawing_review_package
    ADD COLUMN withdrawn_by VARCHAR(100) NULL AFTER approved_at,
    ADD COLUMN withdrawn_at DATETIME(6) NULL AFTER withdrawn_by,
    ADD COLUMN withdrawal_reason VARCHAR(500) NULL AFTER withdrawn_at;
