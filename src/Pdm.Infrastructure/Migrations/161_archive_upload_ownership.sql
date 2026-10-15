CREATE TABLE IF NOT EXISTS archive_upload (
    session_id BINARY(16) NOT NULL PRIMARY KEY,
    batch_id BINARY(16) NOT NULL,
    document_id BINARY(16) NOT NULL,
    project_id BINARY(16) NOT NULL,
    actor VARCHAR(160) NOT NULL,
    checkout_session_id BINARY(16) NOT NULL,
    relative_path VARCHAR(1024) NOT NULL,
    file_length BIGINT NOT NULL,
    sha256 CHAR(64) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    state VARCHAR(16) NOT NULL DEFAULT 'Staged',
    KEY ix_archive_upload_due (state,expires_at),
    KEY ix_archive_upload_batch (batch_id),
    KEY ix_archive_upload_document (document_id),
    KEY ix_archive_upload_path (relative_path(700))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Only new reservations receive a lease. Existing temporary/business data is not backfilled.
ALTER TABLE pending_archive_document ADD COLUMN expires_at DATETIME(6) NULL;
CREATE INDEX ix_pending_archive_expiry ON pending_archive_document(expires_at);
