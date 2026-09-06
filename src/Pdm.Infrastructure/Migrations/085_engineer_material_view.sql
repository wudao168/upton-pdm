INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material.view',UTC_TIMESTAMP(6)
FROM role_definition
WHERE base_role='Engineer'
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
