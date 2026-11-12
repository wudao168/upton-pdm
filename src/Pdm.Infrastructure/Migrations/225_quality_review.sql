CREATE TABLE IF NOT EXISTS quality_review_catalog (
 company_id BINARY(16) NOT NULL PRIMARY KEY,
 payload_json LONGTEXT NOT NULL,
 row_version BIGINT NOT NULL
);
