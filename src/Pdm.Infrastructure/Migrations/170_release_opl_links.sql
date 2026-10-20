SET @pdm_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='related_opl_issue_ids_json'),
    'SELECT 1', 'ALTER TABLE release_package ADD COLUMN related_opl_issue_ids_json JSON NULL');
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;
SET @pdm_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='drawing_document_count'),
    'SELECT 1', 'ALTER TABLE release_package ADD COLUMN drawing_document_count INT NOT NULL DEFAULT 0');
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;
