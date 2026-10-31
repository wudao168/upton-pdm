INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT definition.role_code,'material.notify',UTC_TIMESTAMP(6)
FROM role_definition definition
JOIN role_permission existing ON existing.role_code=definition.role_code AND existing.permission_code='material.view'
WHERE definition.role_code IN ('ProcessReviewer','Approver','StandardizationAssistant');
