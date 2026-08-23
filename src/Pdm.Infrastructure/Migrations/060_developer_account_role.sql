INSERT INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES
('developer','开发者','跨公司访问全部业务数据并拥有全部系统权限。','Administrator',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
    role_name=VALUES(role_name),
    description=VALUES(description),
    base_role='Administrator',
    is_system=1,
    updated_at=UTC_TIMESTAMP(6);

DELETE FROM role_permission WHERE role_code='developer';

INSERT INTO role_permission(role_code,permission_code,updated_at) VALUES
('developer','project.view',UTC_TIMESTAMP(6)),
('developer','project.create',UTC_TIMESTAMP(6)),
('developer','project.edit',UTC_TIMESTAMP(6)),
('developer','project.child.create',UTC_TIMESTAMP(6)),
('developer','project.delete',UTC_TIMESTAMP(6)),
('developer','project.execution.assign',UTC_TIMESTAMP(6)),
('developer','project.staffing.manage',UTC_TIMESTAMP(6)),
('developer','project.designer.assign',UTC_TIMESTAMP(6)),
('developer','project.content.view',UTC_TIMESTAMP(6)),
('developer','document.edit',UTC_TIMESTAMP(6)),
('developer','document.lock.request-release',UTC_TIMESTAMP(6)),
('developer','document.lock.force-release',UTC_TIMESTAMP(6)),
('developer','bom.edit',UTC_TIMESTAMP(6)),
('developer','drawing-review.submit',UTC_TIMESTAMP(6)),
('developer','drawing-review.annotate',UTC_TIMESTAMP(6)),
('developer','drawing-review.decide',UTC_TIMESTAMP(6)),
('developer','release.manage',UTC_TIMESTAMP(6)),
('developer','approval.decide',UTC_TIMESTAMP(6)),
('developer','approval.emergency-substitute',UTC_TIMESTAMP(6)),
('developer','settings.customer.manage',UTC_TIMESTAMP(6)),
('developer','settings.organization.manage',UTC_TIMESTAMP(6)),
('developer','settings.folder.manage',UTC_TIMESTAMP(6)),
('developer','settings.storage.manage',UTC_TIMESTAMP(6)),
('developer','system.role.view',UTC_TIMESTAMP(6)),
('developer','system.role.edit',UTC_TIMESTAMP(6)),
('developer','audit.view',UTC_TIMESTAMP(6));

UPDATE pdm_user
SET display_name='开发者',role='Administrator',assigned_role_code='developer',is_active=1,
    token_version=token_version+1,row_version=row_version+1
WHERE username='developer';
