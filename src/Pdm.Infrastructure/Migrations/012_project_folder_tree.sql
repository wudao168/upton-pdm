CREATE TABLE IF NOT EXISTS folder_template_node (
    folder_key VARCHAR(100) NOT NULL PRIMARY KEY,
    parent_key VARCHAR(100) NULL,
    name VARCHAR(160) NOT NULL,
    purpose VARCHAR(40) NOT NULL,
    sort_order INT NOT NULL DEFAULT 0,
    is_system TINYINT(1) NOT NULL DEFAULT 0,
    inherit_permissions TINYINT(1) NOT NULL DEFAULT 1,
    KEY ix_folder_template_parent (parent_key),
    CONSTRAINT fk_folder_template_parent FOREIGN KEY (parent_key) REFERENCES folder_template_node(folder_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS folder_template_permission (
    id BINARY(16) NOT NULL PRIMARY KEY,
    folder_key VARCHAR(100) NOT NULL,
    principal_type VARCHAR(20) NOT NULL,
    principal_key VARCHAR(100) NOT NULL,
    access_mask INT NOT NULL,
    UNIQUE KEY ux_folder_template_permission (folder_key,principal_type,principal_key),
    CONSTRAINT fk_folder_template_permission_node FOREIGN KEY (folder_key) REFERENCES folder_template_node(folder_key) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_folder (
    id BINARY(16) NOT NULL PRIMARY KEY,
    root_project_id BINARY(16) NOT NULL,
    parent_folder_id BINARY(16) NULL,
    target_project_id BINARY(16) NULL,
    folder_key VARCHAR(180) NOT NULL,
    template_key VARCHAR(100) NOT NULL,
    name VARCHAR(160) NOT NULL,
    purpose VARCHAR(40) NOT NULL,
    sort_order INT NOT NULL DEFAULT 0,
    is_system TINYINT(1) NOT NULL DEFAULT 0,
    inherit_permissions TINYINT(1) NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_project_folder_key (root_project_id,folder_key),
    KEY ix_project_folder_parent (parent_folder_id),
    KEY ix_project_folder_target (target_project_id),
    CONSTRAINT fk_project_folder_root FOREIGN KEY (root_project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_project_folder_parent FOREIGN KEY (parent_folder_id) REFERENCES project_folder(id) ON DELETE CASCADE,
    CONSTRAINT fk_project_folder_target FOREIGN KEY (target_project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_folder_permission (
    id BINARY(16) NOT NULL PRIMARY KEY,
    folder_id BINARY(16) NOT NULL,
    principal_type VARCHAR(20) NOT NULL,
    principal_key VARCHAR(100) NOT NULL,
    access_mask INT NOT NULL,
    UNIQUE KEY ux_project_folder_permission (folder_id,principal_type,principal_key),
    CONSTRAINT fk_project_folder_permission_folder FOREIGN KEY (folder_id) REFERENCES project_folder(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE document
    ADD COLUMN folder_id BINARY(16) NULL AFTER project_id,
    ADD KEY ix_document_folder (folder_id),
    ADD CONSTRAINT fk_document_folder FOREIGN KEY (folder_id) REFERENCES project_folder(id) ON DELETE SET NULL;

INSERT IGNORE INTO folder_template_node(folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions) VALUES
('mechanical',NULL,'机械图纸','MechanicalRoot',10,1,1),
('electrical',NULL,'电气图纸','ElectricalRoot',20,1,1),
('purchase',NULL,'采购清单','Standard',30,1,1),
('production',NULL,'生产资料','Standard',40,1,1),
('project-files',NULL,'项目文件','Standard',50,1,1),
('presales',NULL,'售前资料','Standard',60,1,1),
('customer-files',NULL,'客户资料','Standard',70,1,1),
('acceptance',NULL,'验收资料','Standard',80,1,1),
('media',NULL,'照片视频','Standard',90,1,1),
('minutes',NULL,'会议纪要','Standard',100,1,1);

INSERT IGNORE INTO folder_template_node(folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions) VALUES
('mechanical.project','mechanical','项目目录（自动生成）','ProjectContainer',10,1,1),
('mechanical.air-sequence','mechanical','气路时序','Standard',100,1,1),
('mechanical.nameplate','mechanical','铭牌','Standard',110,1,1),
('mechanical.other','mechanical','其他图纸','Standard',120,1,1),
('mechanical.release','mechanical','机械发布','Release',130,1,1),
('electrical.project','electrical','项目目录（自动生成）','ProjectContainer',10,1,1),
('electrical.release','electrical','电气发布','Release',130,1,1);
