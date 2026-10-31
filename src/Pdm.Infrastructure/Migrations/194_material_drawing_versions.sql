SET @drawing_kind_checks = (SELECT GROUP_CONCAT(CONCAT('DROP CHECK `',tc.CONSTRAINT_NAME,'`') SEPARATOR ',')
    FROM information_schema.TABLE_CONSTRAINTS tc
    JOIN information_schema.CHECK_CONSTRAINTS cc ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME=tc.CONSTRAINT_NAME
    WHERE tc.TABLE_SCHEMA=DATABASE() AND tc.TABLE_NAME='material_attachment' AND cc.CHECK_CLAUSE LIKE '%attachment_kind%');
SET @drawing_kind_sql = IF(@drawing_kind_checks IS NULL,'DO 0',CONCAT('ALTER TABLE material_attachment ',@drawing_kind_checks));
PREPARE drawing_kind_statement FROM @drawing_kind_sql;
EXECUTE drawing_kind_statement;
DEALLOCATE PREPARE drawing_kind_statement;

ALTER TABLE material_attachment
    ADD CONSTRAINT ck_material_attachment_kind CHECK (attachment_kind IN ('Model3D','Document','CoverImage','Drawing2D')),
    ADD COLUMN version_number INT NOT NULL DEFAULT 0,
    ADD COLUMN is_enabled TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN is_removed TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN enabled_by VARCHAR(100) NULL,
    ADD COLUMN enabled_at DATETIME(6) NULL;

UPDATE material_attachment AS a
JOIN (SELECT id, ROW_NUMBER() OVER (PARTITION BY material_id,attachment_kind ORDER BY uploaded_at,id) AS n
      FROM material_attachment WHERE attachment_kind='Model3D') AS numbered ON numbered.id=a.id
SET a.version_number=numbered.n;

CREATE TABLE bom_file_catalog (
    scope_id BINARY(16) NOT NULL PRIMARY KEY,
    payload_json LONGTEXT NOT NULL,
    row_version BIGINT NOT NULL
);
