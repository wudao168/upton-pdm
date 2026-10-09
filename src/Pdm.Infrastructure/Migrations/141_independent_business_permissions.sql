-- Existing built-in roles retain their business capabilities after those checks move to role permissions.
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'project.plan.edit', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('Engineer','ElectricalEngineer','CommissioningEngineer','HardwareEngineer','MechanicalManager','TechnicalAssistant','ProjectManager','developer');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'project.contact.edit', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('ProjectManager','developer');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'project.plan-template.manage', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('Administrator','developer','platform_admin');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'nameplate.template.manage', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('Administrator','developer');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'project.nameplate.edit', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('Engineer','ElectricalEngineer','CommissioningEngineer','HardwareEngineer','MechanicalManager','ProcessReviewer','Approver','ProjectManager','developer');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, permission_code, UTC_TIMESTAMP(6)
FROM role_definition CROSS JOIN (
    SELECT 'quality.incoming.upload' permission_code UNION ALL SELECT 'quality.assembly.upload'
    UNION ALL SELECT 'quality.inspection.upload' UNION ALL SELECT 'quality.pre-acceptance.upload'
    UNION ALL SELECT 'quality.final-acceptance.upload'
) permissions
WHERE role_code IN ('QualityManager','QualityInspector');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, permission_code, UTC_TIMESTAMP(6)
FROM role_definition CROSS JOIN (SELECT 'quality.incoming.upload' permission_code UNION ALL SELECT 'quality.assembly.upload') permissions
WHERE role_code IN ('ProductionManager','ProductionAssistant');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'quality.inspection.upload', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('QualityManager','QualityInspector');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, permission_code, UTC_TIMESTAMP(6)
FROM role_definition CROSS JOIN (SELECT 'quality.pre-acceptance.upload' permission_code UNION ALL SELECT 'quality.final-acceptance.upload') permissions
WHERE role_code IN ('TechnicalAssistant','ProjectManager');
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, permission_code, UTC_TIMESTAMP(6)
FROM role_definition CROSS JOIN (SELECT 'project.budget.reserve' permission_code UNION ALL SELECT 'project.budget.settlement.edit' UNION ALL SELECT 'project.budget.bonus-rate.edit') permissions
WHERE role_code='PlanningManager';
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'project.budget.settlement.edit', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code='SalesAssistant';
INSERT IGNORE INTO role_permission(role_code, permission_code, updated_at)
SELECT role_code, 'project.budget.settlement.view', UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('PlanningManager','SalesAssistant','BusinessUnitManager','Finance');

UPDATE pdm_system_setting SET setting_value=JSON_ARRAY_APPEND(setting_value,'$.rules.MainManager','project.plan.edit')
WHERE setting_key='project_permission_settings' AND JSON_CONTAINS_PATH(setting_value,'one','$.rules.MainManager')
AND NOT JSON_CONTAINS(JSON_EXTRACT(setting_value,'$.rules.MainManager'),JSON_QUOTE('project.plan.edit'));
UPDATE pdm_system_setting SET setting_value=JSON_ARRAY_APPEND(setting_value,'$.rules.ChildManager','project.plan.edit')
WHERE setting_key='project_permission_settings' AND JSON_CONTAINS_PATH(setting_value,'one','$.rules.ChildManager')
AND NOT JSON_CONTAINS(JSON_EXTRACT(setting_value,'$.rules.ChildManager'),JSON_QUOTE('project.plan.edit'));
