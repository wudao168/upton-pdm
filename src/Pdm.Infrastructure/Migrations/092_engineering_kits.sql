CREATE TABLE engineering_kit_counter (
    counter_id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    current_sequence BIGINT NOT NULL
);

INSERT INTO engineering_kit_counter(counter_id,current_sequence) VALUES(1,0);

CREATE TABLE engineering_kit (
    id BINARY(16) NOT NULL PRIMARY KEY,
    kit_code VARCHAR(20) NULL,
    name VARCHAR(160) NOT NULL,
    description VARCHAR(500) NULL,
    current_released_revision_id BINARY(16) NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY ux_engineering_kit_code (kit_code)
);

CREATE TABLE engineering_kit_revision (
    id BINARY(16) NOT NULL PRIMARY KEY,
    kit_id BINARY(16) NOT NULL,
    version_no INT NOT NULL,
    revision_state VARCHAR(20) NOT NULL,
    change_note VARCHAR(500) NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    published_by VARCHAR(100) NULL,
    published_at DATETIME(6) NULL,
    UNIQUE KEY ux_engineering_kit_revision_version (kit_id,version_no),
    KEY ix_engineering_kit_revision_state (kit_id,revision_state),
    CONSTRAINT fk_engineering_kit_revision_kit FOREIGN KEY (kit_id) REFERENCES engineering_kit(id) ON DELETE CASCADE
);

CREATE TABLE engineering_kit_component (
    id BINARY(16) NOT NULL PRIMARY KEY,
    revision_id BINARY(16) NOT NULL,
    material_id BINARY(16) NOT NULL,
    quantity DECIMAL(18,4) NOT NULL,
    is_optional TINYINT(1) NOT NULL DEFAULT 0,
    sort_order INT NOT NULL,
    UNIQUE KEY ux_engineering_kit_component_material (revision_id,material_id),
    KEY ix_engineering_kit_component_revision (revision_id,sort_order),
    CONSTRAINT fk_engineering_kit_component_revision FOREIGN KEY (revision_id) REFERENCES engineering_kit_revision(id) ON DELETE CASCADE,
    CONSTRAINT fk_engineering_kit_component_material FOREIGN KEY (material_id) REFERENCES material_master(id)
);

ALTER TABLE bom_item
    ADD COLUMN engineering_kit_reference_id BINARY(16) NULL AFTER parent_drawing_number,
    ADD COLUMN engineering_kit_id BINARY(16) NULL AFTER engineering_kit_reference_id,
    ADD COLUMN engineering_kit_revision_id BINARY(16) NULL AFTER engineering_kit_id,
    ADD COLUMN engineering_kit_code VARCHAR(20) NULL AFTER engineering_kit_revision_id,
    ADD COLUMN engineering_kit_version_no INT NULL AFTER engineering_kit_code,
    ADD COLUMN engineering_kit_component_id BINARY(16) NULL AFTER engineering_kit_version_no,
    ADD COLUMN engineering_kit_component_optional TINYINT(1) NOT NULL DEFAULT 0 AFTER engineering_kit_component_id,
    ADD KEY ix_bom_item_engineering_kit_reference (project_id,engineering_kit_reference_id);
