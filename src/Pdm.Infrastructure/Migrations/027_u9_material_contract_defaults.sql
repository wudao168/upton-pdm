UPDATE u9_material_integration_setting
SET user_code=CASE WHEN user_code='00004' THEN 'pdm' ELSE user_code END,
    item_create_path=CASE WHEN TRIM(item_create_path)='' THEN '/webapi/ItemMaster/Create' ELSE item_create_path END,
    item_query_path=CASE WHEN TRIM(item_query_path)='' THEN '/webapi/ItemMaster/Query' ELSE item_query_path END,
    updated_by='system',
    updated_at=UTC_TIMESTAMP(6)
WHERE id=1;
