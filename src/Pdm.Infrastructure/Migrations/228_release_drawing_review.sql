SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='drawing_review_json'), 'SELECT 1', 'ALTER TABLE release_package ADD COLUMN drawing_review_json JSON NULL');
PREPARE statement FROM @sql;
EXECUTE statement;
DEALLOCATE PREPARE statement;
