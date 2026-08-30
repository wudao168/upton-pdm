INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material.view',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('ProcessReviewer','Approver')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);

INSERT INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'material.manage',UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('ProcessReviewer','Approver')
ON DUPLICATE KEY UPDATE updated_at=VALUES(updated_at);
