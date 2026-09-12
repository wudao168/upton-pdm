CREATE TABLE IF NOT EXISTS validation_plan_execution_record (
    id BINARY(16) NOT NULL PRIMARY KEY,
    plan_id BINARY(16) NOT NULL,
    source_attachment_id BINARY(16) NOT NULL,
    source_file_name VARCHAR(255) NOT NULL,
    ocr_text LONGTEXT NOT NULL,
    confirmed_by VARCHAR(100) NOT NULL,
    confirmed_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_validation_execution_plan FOREIGN KEY (plan_id) REFERENCES project_validation_plan(id),
    CONSTRAINT fk_validation_execution_attachment FOREIGN KEY (source_attachment_id) REFERENCES validation_plan_attachment(id),
    KEY ix_validation_execution_plan (plan_id,confirmed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS validation_plan_execution_item (
    id BINARY(16) NOT NULL PRIMARY KEY,
    execution_record_id BINARY(16) NOT NULL,
    plan_item_id BINARY(16) NOT NULL,
    match_confidence DECIMAL(5,4) NOT NULL,
    source_text TEXT NOT NULL,
    recognized_result VARCHAR(1500) NULL,
    recognized_validation_date DATE NULL,
    recognized_responsible_person VARCHAR(100) NULL,
    recognized_remark VARCHAR(1000) NULL,
    result VARCHAR(1500) NULL,
    validation_date DATE NULL,
    responsible_person VARCHAR(100) NULL,
    remark VARCHAR(1000) NULL,
    CONSTRAINT fk_validation_execution_item_record FOREIGN KEY (execution_record_id) REFERENCES validation_plan_execution_record(id),
    CONSTRAINT fk_validation_execution_item_plan_item FOREIGN KEY (plan_item_id) REFERENCES project_validation_plan_item(id),
    UNIQUE KEY ux_validation_execution_item (execution_record_id,plan_item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
