CREATE TABLE IF NOT EXISTS mobile_office_layouts (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    payload LONGTEXT NOT NULL,
    row_version BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
