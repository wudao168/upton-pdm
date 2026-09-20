SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pdm_user' AND column_name='theme'), 'SELECT 1', 'ALTER TABLE pdm_user ADD COLUMN theme VARCHAR(8) NOT NULL DEFAULT ''a''');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
