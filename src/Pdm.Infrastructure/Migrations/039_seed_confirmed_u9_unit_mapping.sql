UPDATE u9_material_integration_setting
SET unit_code_mapping_json = JSON_SET(unit_code_mapping_json, '$.EA', '001')
WHERE JSON_EXTRACT(unit_code_mapping_json, '$.EA') IS NULL;
