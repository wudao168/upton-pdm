CREATE TABLE material_code_counter (
    u9_category_code VARCHAR(20) NOT NULL PRIMARY KEY,
    current_value INT UNSIGNED NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
