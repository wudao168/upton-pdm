SET @sql = IF(
    EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='reference_snapshot' AND index_name='ix_reference_snapshot_capturer'),
    'SELECT 1',
    'ALTER TABLE reference_snapshot ADD INDEX ix_reference_snapshot_capturer (captured_by), ALGORITHM=INPLACE, LOCK=NONE'
);
PREPARE pdm_user_reference_ddl FROM @sql;
EXECUTE pdm_user_reference_ddl;
DEALLOCATE PREPARE pdm_user_reference_ddl;
