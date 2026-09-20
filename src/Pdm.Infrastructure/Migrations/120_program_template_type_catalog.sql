-- 程序模板类型改为可维护（类型、编号前缀、审核检查清单组由“选项维护”统一配置），
-- 因此去掉 asset_type 只允许三种内置枚举值的检查约束；内置类型仍然作为默认值保留。
SET @sql = IF(
    (SELECT COUNT(*) FROM information_schema.table_constraints
     WHERE table_schema=DATABASE() AND table_name='program_template' AND constraint_name='ck_program_template_asset_type') > 0,
    'ALTER TABLE program_template DROP CHECK ck_program_template_asset_type',
    'SELECT 1');
PREPARE statement FROM @sql;
EXECUTE statement;
DEALLOCATE PREPARE statement;
