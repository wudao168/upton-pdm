CREATE TABLE IF NOT EXISTS pdm_schema_migration (
    version VARCHAR(64) NOT NULL PRIMARY KEY,
    applied_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS pdm_user (
    id BINARY(16) NOT NULL PRIMARY KEY,
    username VARCHAR(100) NOT NULL,
    display_name VARCHAR(100) NOT NULL,
    password_hash VARCHAR(512) NOT NULL,
    role VARCHAR(40) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    row_version BIGINT NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_pdm_user_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project (
    id BINARY(16) NOT NULL PRIMARY KEY,
    code VARCHAR(80) NOT NULL,
    name VARCHAR(200) NOT NULL,
    owner VARCHAR(100) NOT NULL,
    vault_location VARCHAR(1024) NOT NULL,
    release_location VARCHAR(1024) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    row_version BIGINT NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_project_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS document (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    drawing_number VARCHAR(160) NOT NULL,
    name VARCHAR(300) NOT NULL,
    file_name VARCHAR(512) NOT NULL,
    kind VARCHAR(40) NOT NULL,
    lifecycle_state VARCHAR(40) NOT NULL,
    revision_label VARCHAR(20) NOT NULL,
    checked_out_by VARCHAR(100) NULL,
    checked_out_at DATETIME(6) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_document_project FOREIGN KEY (project_id) REFERENCES project(id),
    UNIQUE KEY ux_document_project_file (project_id, file_name),
    KEY ix_document_project_drawing (project_id, drawing_number),
    FULLTEXT KEY fx_document_search (drawing_number, name, file_name) WITH PARSER ngram
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS document_version (
    id BINARY(16) NOT NULL PRIMARY KEY,
    document_id BINARY(16) NOT NULL,
    revision_label VARCHAR(20) NOT NULL,
    storage_relative_path VARCHAR(1200) NOT NULL,
    file_length BIGINT NOT NULL,
    sha256 CHAR(64) NOT NULL,
    comment VARCHAR(1000) NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_document_version_document FOREIGN KEY (document_id) REFERENCES document(id),
    UNIQUE KEY ux_document_version_revision (document_id, revision_label)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS reference_snapshot (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    root_document_id BINARY(16) NOT NULL,
    captured_at DATETIME(6) NOT NULL,
    captured_by VARCHAR(100) NOT NULL,
    sha256 CHAR(64) NOT NULL,
    root_json JSON NOT NULL,
    CONSTRAINT fk_reference_snapshot_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_reference_snapshot_document FOREIGN KEY (root_document_id) REFERENCES document(id),
    KEY ix_reference_snapshot_project_time (project_id, captured_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS bom_item (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    bom_kind VARCHAR(30) NOT NULL,
    sequence_no INT NOT NULL,
    drawing_number VARCHAR(160) NOT NULL,
    name VARCHAR(300) NOT NULL,
    quantity DECIMAL(18,4) NOT NULL,
    unit VARCHAR(30) NOT NULL,
    material VARCHAR(160) NULL,
    specification VARCHAR(300) NULL,
    revision_label VARCHAR(20) NOT NULL,
    is_complete TINYINT(1) NOT NULL DEFAULT 0,
    row_version BIGINT NOT NULL DEFAULT 1,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_bom_item_project FOREIGN KEY (project_id) REFERENCES project(id),
    UNIQUE KEY ux_bom_item_sequence (project_id, bom_kind, sequence_no),
    KEY ix_bom_item_drawing (project_id, drawing_number)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS release_package (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    package_number VARCHAR(100) NOT NULL,
    state VARCHAR(40) NOT NULL,
    reference_snapshot_id BINARY(16) NOT NULL,
    mechanical_bom_revision VARCHAR(40) NOT NULL,
    electrical_bom_revision VARCHAR(40) NOT NULL,
    published_at DATETIME(6) NULL,
    published_path VARCHAR(1200) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_release_package_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_release_package_snapshot FOREIGN KEY (reference_snapshot_id) REFERENCES reference_snapshot(id),
    UNIQUE KEY ux_release_package_number (project_id, package_number)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS approval_task (
    id BINARY(16) NOT NULL PRIMARY KEY,
    release_package_id BINARY(16) NOT NULL,
    stage VARCHAR(40) NOT NULL,
    assignee VARCHAR(100) NOT NULL,
    decision_by VARCHAR(100) NULL,
    decision_value VARCHAR(30) NULL,
    decision_comment VARCHAR(1000) NULL,
    decided_at DATETIME(6) NULL,
    CONSTRAINT fk_approval_task_package FOREIGN KEY (release_package_id) REFERENCES release_package(id),
    UNIQUE KEY ux_approval_task_stage (release_package_id, stage),
    KEY ix_approval_task_assignee (assignee, decision_value)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS audit_entry (
    id BINARY(16) NOT NULL PRIMARY KEY,
    occurred_at DATETIME(6) NOT NULL,
    actor VARCHAR(100) NOT NULL,
    action_name VARCHAR(120) NOT NULL,
    entity_type VARCHAR(120) NOT NULL,
    entity_id VARCHAR(160) NOT NULL,
    detail_json JSON NOT NULL,
    KEY ix_audit_time (occurred_at),
    KEY ix_audit_entity (entity_type, entity_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS integration_outbox (
    id BINARY(16) NOT NULL PRIMARY KEY,
    event_type VARCHAR(160) NOT NULL,
    aggregate_type VARCHAR(120) NOT NULL,
    aggregate_id VARCHAR(160) NOT NULL,
    payload_json JSON NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    processed_at DATETIME(6) NULL,
    retry_count INT NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    KEY ix_outbox_pending (processed_at, occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
