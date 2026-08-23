UPDATE material_category
SET pdm_kind='Product',
    allow_create=1,
    default_supply_mode='Manufacture',
    updated_by='system',
    updated_at=UTC_TIMESTAMP(6),
    row_version=row_version+1
WHERE category_code='0201'
  AND (pdm_kind IS NULL OR pdm_kind<>'Product' OR allow_create<>1 OR default_supply_mode<>'Manufacture');
