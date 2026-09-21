-- 套件型号支持手动填写或按“标准代码-分类代码-序列号”自动生成，标准代码/分类代码来自可维护选项。
SET @sql = IF(
    (SELECT COUNT(*) FROM information_schema.columns
     WHERE table_schema=DATABASE() AND table_name='engineering_kit' AND column_name='model_mode') = 0,
    'ALTER TABLE engineering_kit ADD COLUMN model_mode VARCHAR(16) NOT NULL DEFAULT ''Auto'' AFTER kit_model',
    'SELECT 1');
PREPARE statement FROM @sql;
EXECUTE statement;
DEALLOCATE PREPARE statement;

SET @sql = IF(
    (SELECT COUNT(*) FROM information_schema.columns
     WHERE table_schema=DATABASE() AND table_name='engineering_kit' AND column_name='standard_code') = 0,
    'ALTER TABLE engineering_kit ADD COLUMN standard_name VARCHAR(60) NULL AFTER model_mode, ADD COLUMN standard_code VARCHAR(32) NULL AFTER standard_name',
    'SELECT 1');
PREPARE statement FROM @sql;
EXECUTE statement;
DEALLOCATE PREPARE statement;

SET @sql = IF(
    (SELECT COUNT(*) FROM information_schema.columns
     WHERE table_schema=DATABASE() AND table_name='engineering_kit' AND column_name='category_code') = 0,
    'ALTER TABLE engineering_kit ADD COLUMN category_name VARCHAR(60) NULL AFTER standard_code, ADD COLUMN category_code VARCHAR(32) NULL AFTER category_name',
    'SELECT 1');
PREPARE statement FROM @sql;
EXECUTE statement;
DEALLOCATE PREPARE statement;
