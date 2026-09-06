CREATE TABLE material_relation_template (
    id BINARY(16) NOT NULL PRIMARY KEY,
    main_material_id BINARY(16) NOT NULL,
    name VARCHAR(160) NOT NULL,
    is_archived TINYINT(1) NOT NULL DEFAULT 0,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY ux_material_relation_template_main (main_material_id),
    CONSTRAINT fk_material_relation_template_main FOREIGN KEY (main_material_id) REFERENCES material_master(id)
);

CREATE TABLE material_relation_revision (
    id BINARY(16) NOT NULL PRIMARY KEY,
    template_id BINARY(16) NOT NULL,
    version_no INT NOT NULL,
    revision_state VARCHAR(30) NOT NULL,
    change_note VARCHAR(500) NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    published_by VARCHAR(100) NULL,
    published_at DATETIME(6) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY ux_material_relation_revision_version (template_id,version_no),
    KEY ix_material_relation_revision_state (template_id,revision_state),
    CONSTRAINT fk_material_relation_revision_template FOREIGN KEY (template_id) REFERENCES material_relation_template(id) ON DELETE CASCADE
);

CREATE TABLE material_relation_group (
    id BINARY(16) NOT NULL PRIMARY KEY,
    revision_id BINARY(16) NOT NULL,
    name VARCHAR(120) NOT NULL,
    is_required TINYINT(1) NOT NULL,
    selection_mode VARCHAR(20) NOT NULL,
    min_selection INT NOT NULL,
    max_selection INT NULL,
    auto_select_unique TINYINT(1) NOT NULL,
    sort_order INT NOT NULL,
    KEY ix_material_relation_group_revision (revision_id,sort_order),
    CONSTRAINT fk_material_relation_group_revision FOREIGN KEY (revision_id) REFERENCES material_relation_revision(id) ON DELETE CASCADE
);

CREATE TABLE material_relation_option (
    id BINARY(16) NOT NULL PRIMARY KEY,
    group_id BINARY(16) NOT NULL,
    material_id BINARY(16) NOT NULL,
    quantity_mode VARCHAR(30) NOT NULL,
    quantity_per_set DECIMAL(18,4) NOT NULL,
    is_default TINYINT(1) NOT NULL,
    sort_order INT NOT NULL,
    UNIQUE KEY ux_material_relation_option_material (group_id,material_id),
    KEY ix_material_relation_option_group (group_id,sort_order),
    CONSTRAINT fk_material_relation_option_group FOREIGN KEY (group_id) REFERENCES material_relation_group(id) ON DELETE CASCADE,
    CONSTRAINT fk_material_relation_option_material FOREIGN KEY (material_id) REFERENCES material_master(id)
);

CREATE TABLE material_relation_selection (
    project_id BINARY(16) NOT NULL,
    main_bom_item_id BINARY(16) NOT NULL,
    accessory_bom_item_id BINARY(16) NOT NULL,
    revision_id BINARY(16) NOT NULL,
    group_id BINARY(16) NOT NULL,
    option_id BINARY(16) NOT NULL,
    expected_quantity DECIMAL(18,4) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY(project_id,main_bom_item_id,group_id,option_id),
    UNIQUE KEY ux_material_relation_selection_accessory (project_id,accessory_bom_item_id),
    KEY ix_material_relation_selection_main (project_id,main_bom_item_id),
    CONSTRAINT fk_material_relation_selection_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_material_relation_selection_revision FOREIGN KEY (revision_id) REFERENCES material_relation_revision(id),
    CONSTRAINT fk_material_relation_selection_group FOREIGN KEY (group_id) REFERENCES material_relation_group(id),
    CONSTRAINT fk_material_relation_selection_option FOREIGN KEY (option_id) REFERENCES material_relation_option(id)
);

INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material-relation.view',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('Engineer','ElectricalEngineer','CommissioningEngineer','HardwareEngineer','MechanicalManager','TechnicalAssistant','ProcessReviewer','Approver','Administrator','developer')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);

INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material-relation.manage',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('ProcessReviewer','Approver','Administrator','developer')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);

INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material-relation.publish',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('Approver','Administrator','developer')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
