INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT 'PlanningManager','project.content.view',UTC_TIMESTAMP(6)
WHERE EXISTS (SELECT 1 FROM role_definition WHERE role_code='PlanningManager')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
