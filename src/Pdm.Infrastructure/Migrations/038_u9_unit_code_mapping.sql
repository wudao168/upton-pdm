ALTER TABLE u9_material_integration_setting
    ADD COLUMN unit_code_mapping_json JSON NULL AFTER item_delete_path;

UPDATE u9_material_integration_setting
SET unit_code_mapping_json = JSON_OBJECT()
WHERE unit_code_mapping_json IS NULL;

ALTER TABLE u9_material_integration_setting
    MODIFY COLUMN unit_code_mapping_json JSON NOT NULL;
