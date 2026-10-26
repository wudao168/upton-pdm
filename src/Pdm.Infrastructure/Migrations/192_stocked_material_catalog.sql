CREATE TABLE stocked_material_catalog (
    company_id BINARY(16) NOT NULL,
    enterprise_code VARCHAR(64) NOT NULL,
    organization_code VARCHAR(64) NOT NULL,
    payload_json LONGTEXT NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    PRIMARY KEY(company_id,enterprise_code,organization_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at)
VALUES('StandardizationAssistant','标准化助理','维护标准物料与常备件库存规划，接收低库存提醒。','Engineer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material.view',UTC_TIMESTAMP(6) FROM role_definition WHERE role_name='标准化助理';
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'standard-library.view',UTC_TIMESTAMP(6) FROM role_definition WHERE role_name='标准化助理';
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'standard-library.manage',UTC_TIMESTAMP(6) FROM role_definition WHERE role_name='标准化助理';
