ALTER TABLE pdm_user
    ADD COLUMN company_id BINARY(16) NULL AFTER assigned_role_code,
    ADD COLUMN cross_company_view TINYINT(1) NOT NULL DEFAULT 0 AFTER company_id;

UPDATE pdm_user user_account
LEFT JOIN (
    SELECT membership.username, MIN(unit.organization_id) company_id
    FROM organization_membership membership
    INNER JOIN organization_unit unit ON unit.id=membership.unit_id
    WHERE membership.is_primary=1
    GROUP BY membership.username
) primary_company ON primary_company.username=user_account.username
SET user_account.company_id=COALESCE(
    primary_company.company_id,
    (SELECT organization.id FROM project_organization organization WHERE organization.is_active=1 ORDER BY organization.name LIMIT 1)
)
WHERE user_account.company_id IS NULL;

ALTER TABLE pdm_user
    MODIFY COLUMN company_id BINARY(16) NOT NULL,
    ADD KEY ix_pdm_user_company (company_id,is_active),
    ADD CONSTRAINT fk_pdm_user_company FOREIGN KEY (company_id) REFERENCES project_organization(id);

CREATE TABLE IF NOT EXISTS user_company_access (
    id BINARY(16) NOT NULL PRIMARY KEY,
    user_id BINARY(16) NOT NULL,
    accessible_company_id BINARY(16) NOT NULL,
    created_by BINARY(16) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_user_company_access (user_id,accessible_company_id),
    KEY ix_user_company_access_company (accessible_company_id,user_id),
    CONSTRAINT fk_user_company_access_user FOREIGN KEY (user_id) REFERENCES pdm_user(id),
    CONSTRAINT fk_user_company_access_company FOREIGN KEY (accessible_company_id) REFERENCES project_organization(id),
    CONSTRAINT fk_user_company_access_creator FOREIGN KEY (created_by) REFERENCES pdm_user(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO user_company_access(id,user_id,accessible_company_id,created_by,created_at)
SELECT UUID_TO_BIN(UUID()),user_account.id,unit.organization_id,NULL,UTC_TIMESTAMP(6)
FROM pdm_user user_account
INNER JOIN organization_membership membership ON membership.username=user_account.username
INNER JOIN organization_unit unit ON unit.id=membership.unit_id
WHERE unit.organization_id<>user_account.company_id;

UPDATE pdm_user user_account
SET cross_company_view=EXISTS(
    SELECT 1 FROM user_company_access access_grant WHERE access_grant.user_id=user_account.id
);

INSERT INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES
('platform_admin','平台管理员','跨公司维护平台设置；不自动获得项目、图档和BOM业务权限。','PlatformAdministrator',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
    role_name=VALUES(role_name),
    description=VALUES(description),
    base_role=VALUES(base_role),
    is_system=1,
    updated_at=UTC_TIMESTAMP(6);

DELETE FROM role_permission WHERE role_code='platform_admin';

INSERT INTO role_permission(role_code,permission_code,updated_at) VALUES
('platform_admin','settings.customer.manage',UTC_TIMESTAMP(6)),
('platform_admin','settings.organization.manage',UTC_TIMESTAMP(6)),
('platform_admin','settings.folder.manage',UTC_TIMESTAMP(6)),
('platform_admin','settings.storage.manage',UTC_TIMESTAMP(6)),
('platform_admin','system.role.view',UTC_TIMESTAMP(6)),
('platform_admin','system.role.edit',UTC_TIMESTAMP(6)),
('platform_admin','audit.view',UTC_TIMESTAMP(6));

UPDATE pdm_user
SET role='PlatformAdministrator',assigned_role_code='platform_admin',token_version=token_version+1,row_version=row_version+1
WHERE username='admin' AND role='Administrator';
