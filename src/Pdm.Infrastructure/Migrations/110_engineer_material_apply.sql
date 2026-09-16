INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role.role_code,'material.apply',UTC_TIMESTAMP(6)
FROM role_definition role
WHERE role.base_role='Engineer'
  AND EXISTS (
      SELECT 1
      FROM role_permission permission
      WHERE permission.role_code=role.role_code
        AND permission.permission_code='bom.edit'
  )
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
