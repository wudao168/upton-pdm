-- Materialize the small native-index lookup keys in a secondary index. Keep
-- unbounded legacy strings in the original JSON query path.
SET @pdm_native_columns = (
    SELECT COUNT(*) FROM information_schema.columns
    WHERE table_schema=DATABASE() AND table_name='reference_snapshot'
        AND column_name IN ('native_inventory_scan_class','native_inventory_configuration','native_inventory_revision')
);
SET @sql = IF(
    @pdm_native_columns=3,
    'SELECT 1',
    'ALTER TABLE reference_snapshot
        ADD COLUMN native_inventory_scan_class TINYINT GENERATED ALWAYS AS (
            CASE WHEN JSON_EXTRACT(root_json,''$.usesNativeFileInventory'')=TRUE THEN
                CASE WHEN COALESCE(CHAR_LENGTH(JSON_UNQUOTE(JSON_EXTRACT(root_json,''$.configuration''))),0)>128
                    OR COALESCE(CHAR_LENGTH(JSON_UNQUOTE(JSON_EXTRACT(root_json,''$.revision.display''))),0)>32
                    THEN 2 ELSE 1 END
            ELSE 0 END
        ) VIRTUAL,
        ADD COLUMN native_inventory_configuration VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
            GENERATED ALWAYS AS (
                CASE WHEN native_inventory_scan_class=1
                    THEN JSON_UNQUOTE(JSON_EXTRACT(root_json,''$.configuration'')) END
            ) VIRTUAL,
        ADD COLUMN native_inventory_revision VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
            GENERATED ALWAYS AS (
                CASE WHEN native_inventory_scan_class=1
                    THEN JSON_UNQUOTE(JSON_EXTRACT(root_json,''$.revision.display'')) END
            ) VIRTUAL,
        ALGORITHM=INSTANT'
);
PREPARE pdm_native_reference_ddl FROM @sql;
EXECUTE pdm_native_reference_ddl;
DEALLOCATE PREPARE pdm_native_reference_ddl;

SET @sql = IF(
    EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='reference_snapshot'
        AND index_name='ix_reference_snapshot_native_scan'),
    'SELECT 1',
    'ALTER TABLE reference_snapshot ADD INDEX ix_reference_snapshot_native_scan
        (project_id,native_inventory_scan_class,root_document_id,captured_at,native_inventory_configuration,native_inventory_revision,id),
        ALGORITHM=INPLACE, LOCK=NONE'
);
PREPARE pdm_native_reference_ddl FROM @sql;
EXECUTE pdm_native_reference_ddl;
DEALLOCATE PREPARE pdm_native_reference_ddl;
