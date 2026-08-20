UPDATE u9_material_integration_setting
SET base_url=LEFT(
        TRIM(TRAILING '/' FROM base_url),
        CHAR_LENGTH(TRIM(TRAILING '/' FROM base_url))-CHAR_LENGTH('/webapi')),
    updated_by='system',
    updated_at=UTC_TIMESTAMP(6)
WHERE LOWER(TRIM(TRAILING '/' FROM base_url)) LIKE '%/webapi';
