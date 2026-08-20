CREATE TABLE IF NOT EXISTS role_definition (
    role_code VARCHAR(100) NOT NULL PRIMARY KEY,
    role_name VARCHAR(100) NOT NULL,
    description VARCHAR(500) NOT NULL DEFAULT '',
    base_role VARCHAR(40) NOT NULL,
    is_system TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_role_definition_name (role_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES
('Engineer','工程师','承担设计、图档、BOM及发布准备工作。','Engineer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('PlanningManager','计划管理','按所属公司分配项目执行事业部。','PlanningManager',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('ProcessReviewer','工艺审核','处理分配给本人的工艺审核任务。','ProcessReviewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('Approver','批准人','处理分配给本人的批准任务。','Approver',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('ProductionViewer','生产查看','按后续项目岗位或目录授权查看生产资料。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('Administrator','系统管理员','固定拥有全部权限，防止系统管理锁死。','Administrator',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
    role_name=VALUES(role_name),
    description=VALUES(description),
    base_role=VALUES(base_role),
    is_system=1,
    updated_at=UTC_TIMESTAMP(6);

ALTER TABLE role_permission MODIFY COLUMN role_code VARCHAR(100) NOT NULL;

ALTER TABLE pdm_user
    ADD COLUMN assigned_role_code VARCHAR(100) NULL AFTER role;

UPDATE pdm_user SET assigned_role_code=role WHERE assigned_role_code IS NULL OR assigned_role_code='';
