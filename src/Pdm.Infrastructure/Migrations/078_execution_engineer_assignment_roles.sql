INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.designer.assign',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('Engineer','MechanicalManager')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
