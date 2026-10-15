CREATE TABLE IF NOT EXISTS pending_archive_document (
    document_id BINARY(16) NOT NULL PRIMARY KEY,
    batch_id BINARY(16) NOT NULL,
    project_id BINARY(16) NOT NULL,
    actor VARCHAR(160) NOT NULL,
    session_id BINARY(16) NOT NULL,
    file_name VARCHAR(260) NOT NULL,
    pending_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_pending_archive_file(batch_id,file_name),
    KEY ix_pending_archive_batch(batch_id)
) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
