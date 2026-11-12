CREATE TABLE IF NOT EXISTS purchase_analysis_settings (
 company_id BINARY(16) NOT NULL PRIMARY KEY, revision BIGINT NOT NULL, payload_json LONGTEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS purchase_analysis_status (
 company_id BINARY(16) NOT NULL PRIMARY KEY, payload_json LONGTEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS purchase_analysis_line (
 company_id BINARY(16) NOT NULL, organization_code VARCHAR(100) NOT NULL, source_id VARCHAR(100) NOT NULL,
 payload_json LONGTEXT NOT NULL, PRIMARY KEY(company_id,organization_code,source_id)
);
