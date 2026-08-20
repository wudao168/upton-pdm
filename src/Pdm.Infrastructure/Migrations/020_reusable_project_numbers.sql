CREATE TABLE IF NOT EXISTS released_project_number (
    organization_id BINARY(16) NOT NULL,
    sequence_value INT NOT NULL,
    released_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (organization_id, sequence_value),
    CONSTRAINT fk_released_project_number_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS released_customer_project_number (
    organization_id BINARY(16) NOT NULL,
    customer_code VARCHAR(64) NOT NULL,
    sequence_value INT NOT NULL,
    released_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (organization_id, customer_code, sequence_value),
    CONSTRAINT fk_released_customer_number_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS released_serial_number (
    organization_id BINARY(16) NOT NULL,
    sequence_value INT NOT NULL,
    released_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (organization_id, sequence_value),
    CONSTRAINT fk_released_serial_number_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB;
