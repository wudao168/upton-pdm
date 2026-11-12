CREATE TABLE IF NOT EXISTS ai_web_usage (
    company_id BINARY(16) NOT NULL,
    period_key VARCHAR(8) NOT NULL,
    calls INT NOT NULL DEFAULT 0,
    PRIMARY KEY (company_id, period_key)
) ENGINE=InnoDB;
