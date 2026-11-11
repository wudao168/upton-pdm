CREATE TABLE IF NOT EXISTS company_business_details (
    organization_id BINARY(16) NOT NULL,
    detail_kind VARCHAR(16) NOT NULL,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL,
    is_configured BOOLEAN NOT NULL,
    PRIMARY KEY (organization_id, detail_kind),
    CONSTRAINT fk_company_business_details_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
