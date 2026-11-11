CREATE TABLE IF NOT EXISTS spare_part_categories (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
