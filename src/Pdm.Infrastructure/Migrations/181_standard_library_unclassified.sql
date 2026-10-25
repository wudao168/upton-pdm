INSERT IGNORE INTO standard_library_category(id,name,parent_id,sort_order,is_active,created_by,created_at,updated_by,updated_at,row_version)
VALUES(UUID_TO_BIN('53544c42-0000-0000-0000-000000000001'),'未分类',NULL,-2147483648,1,'system',UTC_TIMESTAMP(6),'system',UTC_TIMESTAMP(6),1);

UPDATE standard_library_category SET is_active=1,sort_order=-2147483648 WHERE parent_id IS NULL AND name='未分类';
