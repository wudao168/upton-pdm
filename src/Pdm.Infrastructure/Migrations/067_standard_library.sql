ALTER TABLE material_attachment
    DROP CHECK ck_material_attachment_kind,
    ADD CONSTRAINT ck_material_attachment_kind CHECK (attachment_kind IN ('Model3D','Document','CoverImage'));

ALTER TABLE material_master
    ADD COLUMN cover_image_attachment_id BINARY(16) NULL AFTER is_recommended,
    ADD CONSTRAINT fk_material_master_cover_image FOREIGN KEY (cover_image_attachment_id) REFERENCES material_attachment(id) ON DELETE SET NULL;

CREATE TABLE standard_library_category (
    id BINARY(16) NOT NULL PRIMARY KEY,
    name VARCHAR(150) NOT NULL,
    parent_id BINARY(16) NULL,
    parent_scope BINARY(16) GENERATED ALWAYS AS (IFNULL(parent_id,0x00000000000000000000000000000000)) STORED,
    sort_order INT NOT NULL DEFAULT 0,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT fk_standard_library_category_parent FOREIGN KEY (parent_id) REFERENCES standard_library_category(id),
    UNIQUE KEY ux_standard_library_category_parent_name (parent_scope,name),
    KEY ix_standard_library_category_parent_sort (parent_id,sort_order,name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE standard_library_membership (
    category_id BINARY(16) NOT NULL,
    material_id BINARY(16) NOT NULL,
    added_by VARCHAR(100) NOT NULL,
    added_at DATETIME(6) NOT NULL,
    PRIMARY KEY (category_id,material_id),
    CONSTRAINT fk_standard_library_membership_category FOREIGN KEY (category_id) REFERENCES standard_library_category(id),
    CONSTRAINT fk_standard_library_membership_material FOREIGN KEY (material_id) REFERENCES material_master(id),
    KEY ix_standard_library_membership_material (material_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'standard-library.view',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('Engineer','ElectricalEngineer','CommissioningEngineer','HardwareEngineer','MechanicalManager','TechnicalAssistant','ProcessReviewer','Approver','Administrator','developer');

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('Administrator','standard-library.manage',UTC_TIMESTAMP(6)),
('developer','standard-library.manage',UTC_TIMESTAMP(6));
