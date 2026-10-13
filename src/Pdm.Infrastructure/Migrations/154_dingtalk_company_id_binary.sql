-- 早期153部署使用CHAR(36)，Binary16连接将公司GUID写成了16字节。
-- 先保留原始字节，再兼容转换标准文本GUID，最后统一为仓库的BINARY(16)。
ALTER TABLE dingtalk_configuration MODIFY company_id VARBINARY(36) NOT NULL;

UPDATE dingtalk_configuration
SET company_id = UUID_TO_BIN(CONVERT(company_id USING ascii))
WHERE OCTET_LENGTH(company_id) = 36
  AND CONVERT(company_id USING ascii) REGEXP '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';

ALTER TABLE dingtalk_configuration MODIFY company_id BINARY(16) NOT NULL;
