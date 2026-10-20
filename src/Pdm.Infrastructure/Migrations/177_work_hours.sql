CREATE TABLE IF NOT EXISTS work_hours_settings (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    start_date DATE NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS work_hours_month (
    company_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    month_key CHAR(7) NOT NULL,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    PRIMARY KEY (company_id,username,month_key),
    KEY ix_work_hours_month (company_id,month_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
