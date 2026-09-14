CREATE TABLE IF NOT EXISTS project_content_reset_snapshot (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    project_code VARCHAR(100) NOT NULL,
    included_project_ids_json JSON NOT NULL,
    reason VARCHAR(500) NOT NULL,
    summary_json JSON NOT NULL,
    payload_json LONGBLOB NOT NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    restored_by VARCHAR(100) NULL,
    restored_at DATETIME(6) NULL,
    purged_at DATETIME(6) NULL,
    KEY ix_project_content_reset_project (project_id,created_at),
    KEY ix_project_content_reset_expiry (expires_at,restored_at,purged_at),
    CONSTRAINT fk_project_content_reset_project FOREIGN KEY (project_id) REFERENCES project(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE document ADD COLUMN reset_snapshot_id BINARY(16) NULL AFTER purged_at;
ALTER TABLE project_file ADD COLUMN reset_snapshot_id BINARY(16) NULL AFTER deleted_by;

CREATE INDEX ix_document_reset_snapshot ON document(reset_snapshot_id);
CREATE INDEX ix_project_file_reset_snapshot ON project_file(reset_snapshot_id);

INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.content.reset',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('Administrator','developer')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
