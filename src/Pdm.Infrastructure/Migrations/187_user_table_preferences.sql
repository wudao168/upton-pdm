CREATE TABLE IF NOT EXISTS user_table_preference (
    company_id CHAR(36) NOT NULL,
    username VARCHAR(128) NOT NULL,
    preference_hash CHAR(64) NOT NULL,
    preference_key TEXT NOT NULL,
    value_json LONGTEXT NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (company_id, username, preference_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
