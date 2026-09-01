-- Correct only the legacy default; keep explicitly customized mappings.
UPDATE pdm_system_setting
SET setting_value='备注', updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='bom_description_property' AND setting_value='备注信息';
