CREATE TABLE engineering_kit_attachment (
    id BINARY(16) NOT NULL PRIMARY KEY,
    kit_id BINARY(16) NOT NULL,
    attachment_kind VARCHAR(20) NOT NULL,
    original_file_name VARCHAR(255) NOT NULL,
    storage_root VARCHAR(1024) NOT NULL,
    storage_relative_path VARCHAR(1024) NOT NULL,
    file_length BIGINT NOT NULL,
    sha256 VARCHAR(64) NOT NULL,
    uploaded_by VARCHAR(100) NOT NULL,
    uploaded_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_kit_attachment_kit FOREIGN KEY (kit_id) REFERENCES engineering_kit(id),
    CONSTRAINT ck_kit_attachment_kind CHECK (attachment_kind IN ('Model3D','Document')),
    KEY ix_kit_attachment_kind (kit_id,attachment_kind,uploaded_at),
    UNIQUE KEY ux_kit_attachment_path (storage_root(255),storage_relative_path(255))
);
CREATE INDEX ix_bom_engineering_kit_usage ON bom_item(engineering_kit_id,project_id,engineering_kit_reference_id);
