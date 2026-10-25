CREATE TABLE material_stock_plan (
    enterprise_code VARCHAR(64) NOT NULL,
    organization_code VARCHAR(64) NOT NULL,
    material_code VARCHAR(100) NOT NULL,
    safety_stock_quantity DECIMAL(24,9) NOT NULL,
    reorder_quantity DECIMAL(24,9) NULL,
    is_effective TINYINT(1) NOT NULL,
    unit_code VARCHAR(32) NULL,
    unit_name VARCHAR(100) NULL,
    synced_at DATETIME(6) NOT NULL,
    PRIMARY KEY(enterprise_code,organization_code,material_code),
    INDEX ix_stock_plan_active(enterprise_code,organization_code,is_effective,safety_stock_quantity)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
