INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT staffing_role.role_code,'project.designer.assign',UTC_TIMESTAMP(6)
FROM role_permission staffing_role
WHERE staffing_role.permission_code='project.staffing.manage'
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
