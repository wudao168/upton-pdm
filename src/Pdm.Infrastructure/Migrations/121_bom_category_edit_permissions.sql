-- 三类BOM明细的编辑权限按专业拆分，并可在“角色权限”页面自行调整：
--   标准件/非标件BOM → bom.mechanical.edit（默认机械工程师、机械经理、技术助理、调试工程师）
--   电气BOM        → bom.electrical.edit（默认电气工程师、硬件工程师）
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code, 'bom.mechanical.edit', UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('Engineer','MechanicalManager','TechnicalAssistant','CommissioningEngineer');

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code, 'bom.electrical.edit', UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('ElectricalEngineer','HardwareEngineer');
