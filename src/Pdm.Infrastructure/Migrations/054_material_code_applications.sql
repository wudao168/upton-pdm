CREATE TABLE material_code_application (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    bom_item_id BINARY(16) NOT NULL,
    status VARCHAR(30) NOT NULL,
    requested_by VARCHAR(100) NOT NULL,
    requested_at DATETIME(6) NOT NULL,
    decided_by VARCHAR(100) NULL,
    decided_at DATETIME(6) NULL,
    decision_comment VARCHAR(1000) NULL,
    material_id BINARY(16) NULL,
    material_code VARCHAR(160) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT fk_material_application_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_material_application_bom_item FOREIGN KEY (bom_item_id) REFERENCES bom_item(id) ON DELETE CASCADE,
    CONSTRAINT fk_material_application_material FOREIGN KEY (material_id) REFERENCES material_master(id),
    KEY ix_material_application_project_status (project_id,status,requested_at),
    KEY ix_material_application_bom_status (bom_item_id,status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
