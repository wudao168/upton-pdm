-- 发布与转图解耦：发布不再因为转图失败而失败，转图作为单独事项在后台重试并把结果反馈给相关人。
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='preview_state'), 'SELECT 1', 'ALTER TABLE release_package ADD COLUMN preview_state VARCHAR(40) NOT NULL DEFAULT ''None''');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='preview_error'), 'SELECT 1', 'ALTER TABLE release_package ADD COLUMN preview_error VARCHAR(2000) NULL');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='preview_attempts'), 'SELECT 1', 'ALTER TABLE release_package ADD COLUMN preview_attempts INT NOT NULL DEFAULT 0');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='preview_updated_at'), 'SELECT 1', 'ALTER TABLE release_package ADD COLUMN preview_updated_at DATETIME(6) NULL');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
