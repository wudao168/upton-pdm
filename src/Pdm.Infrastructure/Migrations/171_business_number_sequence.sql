CREATE TABLE IF NOT EXISTS business_number_sequence (
    number_prefix VARCHAR(160) NOT NULL PRIMARY KEY,
    current_sequence INT UNSIGNED NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
