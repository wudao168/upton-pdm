ALTER TABLE bom_item
    ADD COLUMN source_document_id BINARY(16) NULL AFTER is_complete,
    ADD COLUMN source_configuration VARCHAR(160) NULL AFTER source_document_id,
    ADD COLUMN item_source VARCHAR(20) NOT NULL DEFAULT 'Manual' AFTER source_configuration,
    ADD COLUMN is_manually_overridden TINYINT(1) NOT NULL DEFAULT 0 AFTER item_source,
    ADD COLUMN is_pending_removal TINYINT(1) NOT NULL DEFAULT 0 AFTER is_manually_overridden,
    ADD KEY ix_bom_item_source (project_id, source_document_id, source_configuration);

UPDATE bom_item SET bom_kind='NonStandard' WHERE bom_kind='Mechanical';

CREATE TABLE project_bom_empty_declaration (
    project_id BINARY(16) NOT NULL,
    bom_kind VARCHAR(30) NOT NULL,
    declared_empty TINYINT(1) NOT NULL DEFAULT 0,
    updated_by VARCHAR(100) NULL,
    updated_at DATETIME(6) NULL,
    PRIMARY KEY (project_id, bom_kind),
    CONSTRAINT fk_project_bom_empty_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES
('bom_drawing_number_property','图号',UTC_TIMESTAMP(6)),
('bom_name_property','名称',UTC_TIMESTAMP(6)),
('bom_description_property','描述',UTC_TIMESTAMP(6)),
('bom_material_property','材料',UTC_TIMESTAMP(6)),
('bom_specification_property','规格',UTC_TIMESTAMP(6)),
('bom_unit_property','单位',UTC_TIMESTAMP(6));
