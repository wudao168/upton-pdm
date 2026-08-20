ALTER TABLE bom_item
    ADD COLUMN is_pending_classification TINYINT(1) NOT NULL DEFAULT 0 AFTER is_pending_removal,
    ADD COLUMN is_manual_unmatched TINYINT(1) NOT NULL DEFAULT 0 AFTER is_pending_classification,
    ADD COLUMN is_manually_retained TINYINT(1) NOT NULL DEFAULT 0 AFTER is_manual_unmatched,
    ADD COLUMN property_writeback_status VARCHAR(30) NULL AFTER is_manually_retained;

CREATE TABLE cad_property_writeback (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    bom_item_id BINARY(16) NOT NULL,
    source_document_id BINARY(16) NOT NULL,
    source_configuration VARCHAR(160) NULL,
    expected_version_id BINARY(16) NOT NULL,
    expected_revision VARCHAR(20) NOT NULL,
    property_payload JSON NOT NULL,
    status VARCHAR(30) NOT NULL,
    requested_by VARCHAR(100) NOT NULL,
    requested_at DATETIME(6) NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    result_version_id BINARY(16) NULL,
    last_error VARCHAR(2000) NULL,
    CONSTRAINT fk_cad_writeback_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_cad_writeback_document FOREIGN KEY (source_document_id) REFERENCES document(id),
    KEY ix_cad_writeback_project_status (project_id, status, requested_at),
    KEY ix_cad_writeback_document_status (source_document_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
