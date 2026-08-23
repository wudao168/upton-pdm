CREATE TABLE IF NOT EXISTS project_bom_header (
    project_id BINARY(16) NOT NULL,
    bom_kind VARCHAR(40) NOT NULL,
    parent_bom_kind VARCHAR(40) NULL,
    material_id BINARY(16) NOT NULL,
    updated_by VARCHAR(120) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    PRIMARY KEY (project_id,bom_kind),
    UNIQUE KEY ux_project_bom_header_material (project_id,material_id),
    CONSTRAINT fk_project_bom_header_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_project_bom_header_material FOREIGN KEY (material_id) REFERENCES material_master(id),
    CONSTRAINT ck_project_bom_header_kind CHECK (bom_kind IN ('Master','Standard','NonStandard','Electrical')),
    CONSTRAINT ck_project_bom_header_parent CHECK (
        (bom_kind='Master' AND parent_bom_kind IS NULL)
        OR (bom_kind<>'Master' AND parent_bom_kind='Master')
    )
);

SET @pdm_schema_name = DATABASE();

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'bom_version' AND column_name = 'mother_material_id'
    ),
    'SELECT 1',
    'ALTER TABLE bom_version ADD COLUMN mother_material_id BINARY(16) NULL AFTER bom_kind'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = @pdm_schema_name AND table_name = 'bom_version' AND index_name = 'ix_bom_version_mother_material'
    ),
    'SELECT 1',
    'ALTER TABLE bom_version ADD KEY ix_bom_version_mother_material (mother_material_id)'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.referential_constraints
        WHERE BINARY constraint_schema = BINARY @pdm_schema_name
          AND BINARY table_name = BINARY 'bom_version'
          AND BINARY constraint_name = BINARY 'fk_bom_version_mother_material'
    ),
    'SELECT 1',
    'ALTER TABLE bom_version ADD CONSTRAINT fk_bom_version_mother_material FOREIGN KEY (mother_material_id) REFERENCES material_master(id)'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

UPDATE material_category
SET pdm_kind='Product',
    allow_create=1,
    default_supply_mode='Manufacture'
WHERE category_code IN ('0301','0302');
