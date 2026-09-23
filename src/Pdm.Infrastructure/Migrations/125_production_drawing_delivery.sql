-- 发图优先级与需求日期是可调整的交付提示；版本文件及审批快照保持不可变。
ALTER TABLE release_package
    ADD COLUMN drawing_priority VARCHAR(12) NOT NULL DEFAULT 'Normal',
    ADD COLUMN drawing_required_on DATE NULL,
    ADD COLUMN drawing_delivery_overrides_json JSON NULL;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code, 'production.drawing.manage', UTC_TIMESTAMP(6)
FROM role_definition
WHERE role_code IN ('ProductionManager','ProductionAssistant');
