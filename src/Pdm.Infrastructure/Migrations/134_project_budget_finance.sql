INSERT IGNORE INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at)
VALUES('Finance','财务','登记项目实际成本及实际工时。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT 'Finance',code,UTC_TIMESTAMP(6) FROM (
 SELECT 'project.view' code UNION ALL SELECT 'project.content.view' UNION ALL SELECT 'project.budget.view' UNION ALL SELECT 'project.budget.actual.edit'
) permissions;
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.budget.actual.edit',UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('Administrator','developer');
