UPDATE material_master
SET unit_code = CASE TRIM(unit_code)
    WHEN '台' THEN '002'
    WHEN '盒' THEN '004'
    WHEN '卷' THEN '005'
    WHEN '捆' THEN '006'
    WHEN '双' THEN '007'
    WHEN '片' THEN '008'
    WHEN '桶' THEN '009'
    WHEN '支' THEN '010'
    WHEN '组' THEN '011'
    WHEN '套' THEN '011'
    WHEN '箱' THEN '012'
    WHEN '包' THEN '013'
    ELSE '001'
END
WHERE UPPER(TRIM(unit_code)) = 'EA'
   OR TRIM(unit_code) IN ('件', '个', '台', '盒', '卷', '捆', '双', '片', '桶', '支', '组', '套', '箱', '包');

UPDATE bom_item
SET unit = CASE TRIM(unit)
    WHEN '台' THEN '002'
    WHEN '盒' THEN '004'
    WHEN '卷' THEN '005'
    WHEN '捆' THEN '006'
    WHEN '双' THEN '007'
    WHEN '片' THEN '008'
    WHEN '桶' THEN '009'
    WHEN '支' THEN '010'
    WHEN '组' THEN '011'
    WHEN '套' THEN '011'
    WHEN '箱' THEN '012'
    WHEN '包' THEN '013'
    ELSE '001'
END
WHERE UPPER(TRIM(unit)) = 'EA'
   OR TRIM(unit) IN ('件', '个', '台', '盒', '卷', '捆', '双', '片', '桶', '支', '组', '套', '箱', '包');

UPDATE u9_material_integration_setting
SET unit_code_mapping_json = JSON_OBJECT();
