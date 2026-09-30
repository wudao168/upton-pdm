INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'system.project-permission.view',UTC_TIMESTAMP(6)
FROM role_definition WHERE role_code IN ('Administrator','platform_admin','developer');

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'system.project-permission.edit',UTC_TIMESTAMP(6)
FROM role_definition WHERE role_code IN ('Administrator','platform_admin','developer');
