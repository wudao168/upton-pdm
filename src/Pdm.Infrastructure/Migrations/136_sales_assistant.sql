INSERT IGNORE INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at)
VALUES('SalesAssistant','销售助理','查看项目预算并维护项目结算明细。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('SalesAssistant','project.view',UTC_TIMESTAMP(6)),
('SalesAssistant','project.content.view',UTC_TIMESTAMP(6)),
('SalesAssistant','project.budget.view',UTC_TIMESTAMP(6));
