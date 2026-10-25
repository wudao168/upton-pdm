CREATE TABLE IF NOT EXISTS work_hours_management (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
