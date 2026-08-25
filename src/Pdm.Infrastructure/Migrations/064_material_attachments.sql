CREATE TABLE material_attachment (
    id BINARY(16) NOT NULL PRIMARY KEY,
    material_id BINARY(16) NOT NULL,
    attachment_kind VARCHAR(30) NOT NULL,
    original_file_name VARCHAR(500) NOT NULL,
    storage_root VARCHAR(1000) NOT NULL,
    storage_relative_path VARCHAR(1000) NOT NULL,
    file_length BIGINT NOT NULL,
    sha256 CHAR(64) NOT NULL,
    uploaded_by VARCHAR(100) NOT NULL,
    uploaded_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_material_attachment_material FOREIGN KEY (material_id) REFERENCES material_master(id),
    CONSTRAINT ck_material_attachment_kind CHECK (attachment_kind IN ('Model3D','Document')),
    UNIQUE KEY ux_material_attachment_path (storage_root(255),storage_relative_path(255)),
    KEY ix_material_attachment_material_kind (material_id,attachment_kind,uploaded_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at)
VALUES('material_attachment_root','D:\\PDM\\MaterialAttachments',UTC_TIMESTAMP(6));
