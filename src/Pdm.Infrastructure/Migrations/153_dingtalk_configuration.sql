CREATE TABLE IF NOT EXISTS dingtalk_configuration (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    protected_payload LONGTEXT NOT NULL,
    row_version BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
