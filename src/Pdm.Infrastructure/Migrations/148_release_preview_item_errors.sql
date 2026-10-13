-- 保留逐项转图失败原因，避免大批次超过原有 2000 字符限制而丢失成功结果。
ALTER TABLE release_package MODIFY COLUMN preview_error MEDIUMTEXT NULL;
