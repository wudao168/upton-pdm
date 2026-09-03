CREATE TABLE release_item_comment (
    id BINARY(16) NOT NULL,
    release_package_id BINARY(16) NOT NULL,
    bom_item_id BINARY(16) NOT NULL,
    material_key VARCHAR(400) NOT NULL,
    material_code VARCHAR(200) NOT NULL,
    material_name VARCHAR(500) NOT NULL,
    specification VARCHAR(500) NULL,
    source_instance_path VARCHAR(1000) NULL,
    comment_text VARCHAR(1000) NOT NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_release_item_comment_package_material (release_package_id, material_key, created_at),
    CONSTRAINT fk_release_item_comment_package
        FOREIGN KEY (release_package_id) REFERENCES release_package(id) ON DELETE CASCADE
);
