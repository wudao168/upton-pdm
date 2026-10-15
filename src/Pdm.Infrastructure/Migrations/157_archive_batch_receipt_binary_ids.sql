-- 早期155已部署为CHAR(36)，修改原迁移不会更新已安装的数据库。
-- 保留已有回执，将文本GUID及原始二进制GUID统一为Binary16连接使用的格式。
ALTER TABLE archive_batch_receipt
    MODIFY batch_id VARBINARY(36) NOT NULL,
    MODIFY project_id VARBINARY(36) NOT NULL;

UPDATE archive_batch_receipt
SET batch_id = UUID_TO_BIN(CONVERT(batch_id USING ascii))
WHERE OCTET_LENGTH(batch_id) = 36
  AND CONVERT(batch_id USING ascii) REGEXP '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';

UPDATE archive_batch_receipt
SET project_id = UUID_TO_BIN(CONVERT(project_id USING ascii))
WHERE OCTET_LENGTH(project_id) = 36
  AND CONVERT(project_id USING ascii) REGEXP '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';

ALTER TABLE archive_batch_receipt
    MODIFY batch_id BINARY(16) NOT NULL,
    MODIFY project_id BINARY(16) NOT NULL;
