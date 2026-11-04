-- 已安装的202版曾只包含整套倍率列，补齐覆盖标志；不改写已有倍率或发布快照。
SET @pdm_add_multiplier_override = NOT EXISTS(
    SELECT 1 FROM information_schema.columns
    WHERE table_schema=DATABASE() AND table_name='bom_version' AND column_name='is_multiplier_overridden'
);
SET @pdm_multiplier_sql = IF(@pdm_add_multiplier_override,
    'ALTER TABLE bom_version ADD COLUMN is_multiplier_overridden TINYINT(1) NOT NULL DEFAULT 0', 'SELECT 1');
PREPARE pdm_multiplier_statement FROM @pdm_multiplier_sql;
EXECUTE pdm_multiplier_statement;
DEALLOCATE PREPARE pdm_multiplier_statement;
SET @pdm_multiplier_sql = IF(@pdm_add_multiplier_override,
    'UPDATE bom_version SET is_multiplier_overridden=(whole_set_multiplier<>1)', 'SELECT 1');
PREPARE pdm_multiplier_statement FROM @pdm_multiplier_sql;
EXECUTE pdm_multiplier_statement;
DEALLOCATE PREPARE pdm_multiplier_statement;
