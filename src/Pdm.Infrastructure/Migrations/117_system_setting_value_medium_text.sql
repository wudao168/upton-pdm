-- 系统设置里保存的是 JSON 片段（BOM 属性映射、审批流程模板、二维码规则等），VARCHAR(1000) 会被写满并报
-- "Data too long for column 'setting_value'"，导致系统设置（含 BOM 资料校验规则）无法保存。
SET @sql = IF((SELECT data_type FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pdm_system_setting' AND column_name='setting_value') = 'mediumtext', 'SELECT 1', 'ALTER TABLE pdm_system_setting MODIFY COLUMN setting_value MEDIUMTEXT NOT NULL');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
