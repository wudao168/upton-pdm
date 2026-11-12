CREATE TABLE IF NOT EXISTS ai_settings (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    revision BIGINT NOT NULL,
    payload_json LONGTEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ai_conversations (
    id BINARY(16) NOT NULL PRIMARY KEY,
    company_id BINARY(16) NOT NULL,
    user_id BINARY(16) NOT NULL,
    revision BIGINT NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    payload_json LONGTEXT NOT NULL,
    INDEX ix_ai_owner (company_id,user_id,updated_at)
);
