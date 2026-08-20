INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES
('bom_standard_required_fields','["drawingNumber","name","unit","specification","quantity","revision"]',UTC_TIMESTAMP(6)),
('bom_nonstandard_required_fields','["drawingNumber","name","unit","material","quantity","revision"]',UTC_TIMESTAMP(6)),
('bom_electrical_required_fields','["drawingNumber","name","unit","quantity","revision"]',UTC_TIMESTAMP(6));

ALTER TABLE bom_version
    ADD COLUMN validation_rule_snapshot_json JSON NULL AFTER snapshot_json;
