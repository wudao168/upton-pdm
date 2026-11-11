CREATE TABLE IF NOT EXISTS project_file_form (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    form_kind VARCHAR(30) NOT NULL,
    form_sequence INT NOT NULL,
    form_number VARCHAR(180) NOT NULL,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL,
    archived_revision BIGINT NOT NULL DEFAULT 0,
    file_id BINARY(16) NULL,
    file_version INT NOT NULL DEFAULT 0,
    evidence_file_id BINARY(16) NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_project_file_form_sequence (project_id, form_kind, form_sequence),
    CONSTRAINT fk_project_file_form_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
