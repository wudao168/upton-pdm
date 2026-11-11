CREATE TABLE IF NOT EXISTS spare_part (
    company_id BINARY(16) NOT NULL,
    id BINARY(16) NOT NULL,
    project_id BINARY(16) NOT NULL,
    identity_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    PRIMARY KEY (company_id, id),
    UNIQUE KEY uq_spare_part_identity (company_id, project_id, identity_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS spare_part_pricing (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS spare_part_purchase (
    company_id BINARY(16) NOT NULL,
    organization_code VARCHAR(50) NOT NULL,
    currency CHAR(3) NOT NULL,
    material_code VARCHAR(160) NOT NULL,
    synced_at DATETIME(6) NOT NULL,
    payload_json JSON NOT NULL,
    PRIMARY KEY (company_id, organization_code, currency, material_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
