-- 保留其他角色的现有入口与操作。仅迁移一次，后续管理员配置不会被覆盖。
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.tab.overview.view',UTC_TIMESTAMP(6) FROM role_permission WHERE permission_code='project.view';
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.tab.records.view',UTC_TIMESTAMP(6) FROM role_permission WHERE permission_code='project.view';
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT existing.role_code,permission.code,UTC_TIMESTAMP(6) FROM role_permission existing
CROSS JOIN (
    SELECT 'project.tab.information.view' code UNION ALL SELECT 'project.tab.files.view'
    UNION ALL SELECT 'project.tab.project-plan.view' UNION ALL SELECT 'project.tab.opl.view'
    UNION ALL SELECT 'project.tab.budget.view' UNION ALL SELECT 'project.tab.validation-plan.view'
    UNION ALL SELECT 'project.tab.documents.view' UNION ALL SELECT 'project.tab.bom.view'
    UNION ALL SELECT 'project.tab.release.view' UNION ALL SELECT 'project.tab.procurement.view'
    UNION ALL SELECT 'project.content.write' UNION ALL SELECT 'project.content.download'
    UNION ALL SELECT 'project.content.export' UNION ALL SELECT 'project.content.print'
) permission WHERE existing.permission_code='project.content.view';

DELETE FROM role_permission WHERE role_code IN ('AssemblySupervisor','AssemblyFitter','ElectricalSupervisor','AssemblyElectrician');
INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT definition.role_code,permission.code,UTC_TIMESTAMP(6) FROM role_definition definition
CROSS JOIN (
    SELECT 'system.permission-profile.v2' code UNION ALL SELECT 'project.view' UNION ALL SELECT 'project.content.view'
    UNION ALL SELECT 'project.tab.overview.view' UNION ALL SELECT 'project.tab.project-plan.view'
    UNION ALL SELECT 'project.tab.opl.view' UNION ALL SELECT 'project.tab.validation-plan.view'
    UNION ALL SELECT 'project.tab.documents.view' UNION ALL SELECT 'project.tab.bom.view'
    UNION ALL SELECT 'project.tab.release.view' UNION ALL SELECT 'project.tab.procurement.view'
    UNION ALL SELECT 'project.plan.view' UNION ALL SELECT 'project.opl.view' UNION ALL SELECT 'document.view'
    UNION ALL SELECT 'release.view' UNION ALL SELECT 'bom.mechanical.row.view' UNION ALL SELECT 'bom.electrical.row.view'
) permission WHERE definition.role_code IN ('AssemblySupervisor','AssemblyFitter','ElectricalSupervisor','AssemblyElectrician');

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'system.project-center-permissions.v1',UTC_TIMESTAMP(6) FROM role_definition;
