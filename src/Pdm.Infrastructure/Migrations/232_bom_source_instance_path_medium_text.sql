-- Validate both columns and the existing prefix index before either ALTER.
SET @pdm_232_columns = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('bom_item','release_item_comment')
        AND COLUMN_NAME='source_instance_path' AND COLUMN_TYPE IN ('varchar(1000)','mediumtext')
        AND CHARACTER_SET_NAME='utf8mb4' AND COLLATION_NAME='utf8mb4_0900_ai_ci'
        AND IS_NULLABLE='YES' AND COLUMN_DEFAULT IS NULL
);
SET @pdm_232_index = (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='bom_item' AND INDEX_NAME='ix_bom_item_source_occurrence'
        AND INDEX_TYPE='BTREE' AND IS_VISIBLE='YES' AND NON_UNIQUE=1
        AND ((SEQ_IN_INDEX=1 AND COLUMN_NAME='project_id' AND SUB_PART IS NULL)
            OR (SEQ_IN_INDEX=2 AND COLUMN_NAME='source_document_id' AND SUB_PART IS NULL)
            OR (SEQ_IN_INDEX=3 AND COLUMN_NAME='source_instance_path' AND SUB_PART=191))
);
SET @pdm_232_index_total = (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='bom_item' AND INDEX_NAME='ix_bom_item_source_occurrence'
);
SET @pdm_232_statement = IF(@pdm_232_columns=2 AND @pdm_232_index=3 AND @pdm_232_index_total=3,
    'SELECT 1', 'SELECT uplm_232_bom_source_instance_path_schema_guard_failed');
PREPARE pdm_232_bom_source_path FROM @pdm_232_statement;
EXECUTE pdm_232_bom_source_path;
DEALLOCATE PREPARE pdm_232_bom_source_path;

SET @pdm_232_statement = IF(EXISTS(
    SELECT 1 FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='bom_item'
        AND COLUMN_NAME='source_instance_path' AND COLUMN_TYPE='varchar(1000)'),
    'ALTER TABLE bom_item MODIFY COLUMN source_instance_path MEDIUMTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL',
    'SELECT 1');
PREPARE pdm_232_bom_source_path FROM @pdm_232_statement;
EXECUTE pdm_232_bom_source_path;
DEALLOCATE PREPARE pdm_232_bom_source_path;

SET @pdm_232_statement = IF(EXISTS(
    SELECT 1 FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='release_item_comment'
        AND COLUMN_NAME='source_instance_path' AND COLUMN_TYPE='varchar(1000)'),
    'ALTER TABLE release_item_comment MODIFY COLUMN source_instance_path MEDIUMTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL',
    'SELECT 1');
PREPARE pdm_232_bom_source_path FROM @pdm_232_statement;
EXECUTE pdm_232_bom_source_path;
DEALLOCATE PREPARE pdm_232_bom_source_path;
