CREATE TABLE IF NOT EXISTS archive_batch_receipt (
    batch_id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    actor VARCHAR(120) NOT NULL,
    request_hash CHAR(64) NOT NULL,
    result_json LONGTEXT NULL,
    committed_at DATETIME(6) NULL
);
