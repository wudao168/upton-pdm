CREATE TABLE IF NOT EXISTS bom_version (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    bom_kind VARCHAR(40) NOT NULL,
    version_number INT NOT NULL,
    version_label VARCHAR(40) NOT NULL,
    state VARCHAR(40) NOT NULL,
    base_version_id BINARY(16) NULL,
    change_number VARCHAR(100) NULL,
    change_reason VARCHAR(1000) NULL,
    effective_serial_from VARCHAR(80) NULL,
    effective_serial_to VARCHAR(80) NULL,
    snapshot_json JSON NOT NULL,
    created_by VARCHAR(120) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(120) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    released_at DATETIME(6) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT fk_bom_version_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_bom_version_base FOREIGN KEY (base_version_id) REFERENCES bom_version(id),
    UNIQUE KEY ux_bom_version_number (project_id, bom_kind, version_number),
    UNIQUE KEY ux_bom_version_label (project_id, bom_kind, version_label),
    KEY ix_bom_version_state (project_id, bom_kind, state, version_number)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE release_package
    ADD COLUMN standard_bom_version_id BINARY(16) NULL AFTER electrical_bom_revision,
    ADD COLUMN non_standard_bom_version_id BINARY(16) NULL AFTER standard_bom_version_id,
    ADD COLUMN electrical_bom_version_id BINARY(16) NULL AFTER non_standard_bom_version_id,
    ADD COLUMN standard_bom_revision VARCHAR(40) NULL AFTER electrical_bom_version_id,
    ADD COLUMN non_standard_bom_revision VARCHAR(40) NULL AFTER standard_bom_revision,
    ADD COLUMN standard_bom_snapshot_json JSON NULL AFTER electrical_bom_snapshot_json,
    ADD COLUMN non_standard_bom_snapshot_json JSON NULL AFTER standard_bom_snapshot_json,
    ADD COLUMN change_number VARCHAR(100) NULL AFTER non_standard_bom_snapshot_json,
    ADD COLUMN change_reason VARCHAR(1000) NULL AFTER change_number,
    ADD COLUMN effective_serial_from VARCHAR(80) NULL AFTER change_reason,
    ADD COLUMN effective_serial_to VARCHAR(80) NULL AFTER effective_serial_from,
    ADD CONSTRAINT fk_release_standard_bom_version FOREIGN KEY (standard_bom_version_id) REFERENCES bom_version(id),
    ADD CONSTRAINT fk_release_nonstandard_bom_version FOREIGN KEY (non_standard_bom_version_id) REFERENCES bom_version(id),
    ADD CONSTRAINT fk_release_electrical_bom_version FOREIGN KEY (electrical_bom_version_id) REFERENCES bom_version(id);

UPDATE release_package
SET standard_bom_snapshot_json = JSON_ARRAY()
WHERE standard_bom_snapshot_json IS NULL;

UPDATE release_package
SET non_standard_bom_snapshot_json = JSON_ARRAY()
WHERE non_standard_bom_snapshot_json IS NULL;

ALTER TABLE release_package
    MODIFY standard_bom_snapshot_json JSON NOT NULL,
    MODIFY non_standard_bom_snapshot_json JSON NOT NULL;

CREATE TABLE IF NOT EXISTS manufacturing_bom_baseline (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    sequence_no INT NOT NULL,
    baseline_label VARCHAR(40) NOT NULL,
    standard_bom_version_id BINARY(16) NOT NULL,
    non_standard_bom_version_id BINARY(16) NOT NULL,
    electrical_bom_version_id BINARY(16) NOT NULL,
    change_number VARCHAR(100) NOT NULL,
    change_reason VARCHAR(1000) NOT NULL,
    effective_serial_from VARCHAR(80) NOT NULL,
    effective_serial_to VARCHAR(80) NULL,
    release_package_id BINARY(16) NOT NULL,
    created_by VARCHAR(120) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_bom_baseline_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_bom_baseline_standard FOREIGN KEY (standard_bom_version_id) REFERENCES bom_version(id),
    CONSTRAINT fk_bom_baseline_nonstandard FOREIGN KEY (non_standard_bom_version_id) REFERENCES bom_version(id),
    CONSTRAINT fk_bom_baseline_electrical FOREIGN KEY (electrical_bom_version_id) REFERENCES bom_version(id),
    CONSTRAINT fk_bom_baseline_release_package FOREIGN KEY (release_package_id) REFERENCES release_package(id),
    UNIQUE KEY ux_bom_baseline_sequence (project_id, sequence_no),
    UNIQUE KEY ux_bom_baseline_label (project_id, baseline_label),
    UNIQUE KEY ux_bom_baseline_release_package (release_package_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
