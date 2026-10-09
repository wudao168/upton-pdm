INSERT IGNORE INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES
('PresalesEngineer','售前工程师','负责项目前期资料的提供，参与评审。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('SalesManager','销售经理','查看自己当前项目的状态。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('PresalesEngineer','project.view',UTC_TIMESTAMP(6)),
('PresalesEngineer','project.content.view',UTC_TIMESTAMP(6)),
('SalesManager','project.view',UTC_TIMESTAMP(6)),
('SalesManager','project.content.view',UTC_TIMESTAMP(6));

INSERT IGNORE INTO folder_template_permission(id,folder_key,principal_type,principal_key,access_mask)
SELECT UUID_TO_BIN(UUID()),defaults.folder_key,'Role',defaults.role_code,7
FROM (
    SELECT 'presales' folder_key,'PresalesEngineer' role_code UNION ALL
    SELECT 'presales','SalesManager' UNION ALL
    SELECT 'customer-files','PresalesEngineer' UNION ALL
    SELECT 'customer-files','SalesManager'
) defaults
INNER JOIN folder_template_node node ON node.folder_key=defaults.folder_key
INNER JOIN role_definition role ON role.role_code=defaults.role_code;
