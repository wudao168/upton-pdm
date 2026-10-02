INSERT IGNORE INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES
('QualityManager','质量经理','管理和查看项目质量检验、过程检验及客户验收资料。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
('QualityInspector','质量员','上传、维护和查看项目质量相关检验及验收资料。','ProductionViewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('QualityManager','project.view',UTC_TIMESTAMP(6)),
('QualityManager','project.content.view',UTC_TIMESTAMP(6)),
('QualityManager','validation-plan.edit',UTC_TIMESTAMP(6)),
('QualityInspector','project.view',UTC_TIMESTAMP(6)),
('QualityInspector','project.content.view',UTC_TIMESTAMP(6)),
('QualityInspector','validation-plan.edit',UTC_TIMESTAMP(6));
