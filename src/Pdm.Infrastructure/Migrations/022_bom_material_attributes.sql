ALTER TABLE bom_item
    ADD COLUMN remark VARCHAR(1000) NULL AFTER specification,
    ADD COLUMN brand VARCHAR(200) NULL AFTER remark,
    ADD COLUMN surface_treatment VARCHAR(500) NULL AFTER brand,
    ADD COLUMN weight VARCHAR(100) NULL AFTER surface_treatment;

UPDATE pdm_system_setting SET setting_value='物料编码', updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='bom_drawing_number_property' AND setting_value='图号';
UPDATE pdm_system_setting SET setting_value='物料名称', updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='bom_name_property' AND setting_value='名称';
UPDATE pdm_system_setting SET setting_value='备注信息', updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='bom_description_property' AND setting_value='描述';
UPDATE pdm_system_setting SET setting_value='材质', updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='bom_material_property' AND setting_value='材料';
UPDATE pdm_system_setting SET setting_value='型号', updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='bom_specification_property' AND setting_value='规格';

INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES
('bom_brand_property','品牌',UTC_TIMESTAMP(6)),
('bom_surface_treatment_property','表面处理',UTC_TIMESTAMP(6)),
('bom_weight_property','重量',UTC_TIMESTAMP(6));
