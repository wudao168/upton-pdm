CREATE TABLE IF NOT EXISTS archive_document_recycle_set (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    batch_id BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    restored_at DATETIME(6) NULL,
    payload_json LONGTEXT NOT NULL,
    KEY ix_archive_recycle_project(project_id,created_at)
);
