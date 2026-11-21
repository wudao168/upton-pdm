SET @sql = IF(
    EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='audit_entry' AND index_name='ix_audit_actor'),
    'SELECT 1',
    'ALTER TABLE audit_entry ADD INDEX ix_audit_actor (actor), ALGORITHM=INPLACE, LOCK=NONE'
);
PREPARE pdm_user_reference_ddl FROM @sql;
EXECUTE pdm_user_reference_ddl;
DEALLOCATE PREPARE pdm_user_reference_ddl;

SET @sql = IF(
    EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='document_version' AND index_name='ix_document_version_creator'),
    'SELECT 1',
    'ALTER TABLE document_version ADD INDEX ix_document_version_creator (created_by), ALGORITHM=INPLACE, LOCK=NONE'
);
PREPARE pdm_user_reference_ddl FROM @sql;
EXECUTE pdm_user_reference_ddl;
DEALLOCATE PREPARE pdm_user_reference_ddl;
