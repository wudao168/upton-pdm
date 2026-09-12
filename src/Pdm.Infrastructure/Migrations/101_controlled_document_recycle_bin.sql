ALTER TABLE document
    DROP INDEX ux_document_project_file,
    ADD COLUMN deleted_at DATETIME(6) NULL AFTER updated_at,
    ADD COLUMN deleted_by VARCHAR(100) NULL AFTER deleted_at,
    ADD COLUMN delete_reason VARCHAR(500) NULL AFTER deleted_by,
    ADD COLUMN purged_at DATETIME(6) NULL AFTER delete_reason,
    ADD COLUMN active_file_name VARCHAR(512)
        GENERATED ALWAYS AS (CASE WHEN purged_at IS NULL THEN file_name ELSE NULL END) STORED AFTER purged_at,
    ADD KEY ix_document_recycle_cleanup (deleted_at, purged_at),
    ADD UNIQUE KEY ux_document_project_active_file (project_id, active_file_name);

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('Administrator','document.recycle',UTC_TIMESTAMP(6)),
('developer','document.recycle',UTC_TIMESTAMP(6));
