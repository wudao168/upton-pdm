CREATE TABLE material_category_rule (
    pdm_kind VARCHAR(30) NOT NULL PRIMARY KEY,
    u9_category_code VARCHAR(20) NOT NULL,
    u9_category_name VARCHAR(100) NOT NULL,
    default_supply_mode VARCHAR(30) NOT NULL,
    is_enabled TINYINT(1) NOT NULL DEFAULT 1,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_material_category_rule_u9_code (u9_category_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO material_category_rule(
    pdm_kind,u9_category_code,u9_category_name,default_supply_mode,is_enabled,updated_by,updated_at)
VALUES
    ('Electrical','0101','电气外购件','Purchase',1,'system',UTC_TIMESTAMP(6)),
    ('Standard','0102','机械外购件','Purchase',1,'system',UTC_TIMESTAMP(6)),
    ('NonStandard','0204','非标机加件','Manufacture',1,'system',UTC_TIMESTAMP(6));

CREATE TABLE material_master (
    id BINARY(16) NOT NULL PRIMARY KEY,
    material_code VARCHAR(160) NOT NULL,
    name VARCHAR(300) NOT NULL,
    material_kind VARCHAR(30) NOT NULL,
    supply_mode VARCHAR(30) NOT NULL,
    unit_code VARCHAR(80) NOT NULL,
    specification VARCHAR(500) NULL,
    material VARCHAR(300) NULL,
    remark VARCHAR(2000) NULL,
    brand VARCHAR(300) NULL,
    surface_treatment VARCHAR(300) NULL,
    weight DECIMAL(18,6) NULL,
    weight_unit VARCHAR(30) NULL,
    source_bom_item_id BINARY(16) NULL,
    approval_status VARCHAR(30) NOT NULL,
    approved_by VARCHAR(100) NULL,
    approved_at DATETIME(6) NULL,
    u9_category_code VARCHAR(20) NULL,
    u9_item_id VARCHAR(160) NULL,
    u9_item_code VARCHAR(160) NULL,
    sync_status VARCHAR(30) NOT NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY ux_material_master_code (material_code),
    KEY ix_material_master_status (approval_status,sync_status,updated_at),
    KEY ix_material_master_source_bom (source_bom_item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE bom_material_link (
    bom_item_id BINARY(16) NOT NULL PRIMARY KEY,
    material_id BINARY(16) NOT NULL,
    linked_by VARCHAR(100) NOT NULL,
    linked_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_bom_material_link_bom FOREIGN KEY (bom_item_id) REFERENCES bom_item(id) ON DELETE CASCADE,
    CONSTRAINT fk_bom_material_link_material FOREIGN KEY (material_id) REFERENCES material_master(id),
    KEY ix_bom_material_link_material (material_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE u9_material_sync_task (
    id BINARY(16) NOT NULL PRIMARY KEY,
    material_id BINARY(16) NOT NULL,
    operation VARCHAR(30) NOT NULL,
    status VARCHAR(30) NOT NULL,
    correlation_id VARCHAR(64) NOT NULL,
    payload_json JSON NOT NULL,
    payload_sha256 CHAR(64) NOT NULL,
    attempt_count INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NULL,
    last_error VARCHAR(2000) NULL,
    response_preview JSON NULL,
    u9_item_id VARCHAR(160) NULL,
    u9_item_code VARCHAR(160) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_u9_material_sync_task_material FOREIGN KEY (material_id) REFERENCES material_master(id),
    UNIQUE KEY ux_u9_material_sync_correlation (correlation_id),
    UNIQUE KEY ux_u9_material_sync_payload (material_id,operation,payload_sha256),
    KEY ix_u9_material_sync_status (status,next_attempt_at,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE u9_material_integration_setting (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    base_url VARCHAR(500) NOT NULL,
    enterprise_code VARCHAR(50) NOT NULL,
    organization_code VARCHAR(50) NOT NULL,
    user_code VARCHAR(100) NOT NULL,
    client_id VARCHAR(100) NOT NULL,
    client_secret_ciphertext TEXT NOT NULL,
    item_create_path VARCHAR(500) NOT NULL,
    item_query_path VARCHAR(500) NOT NULL,
    write_enabled TINYINT(1) NOT NULL DEFAULT 0,
    updated_by VARCHAR(100) NULL,
    updated_at DATETIME(6) NULL,
    CONSTRAINT ck_u9_material_setting_singleton CHECK (id=1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO u9_material_integration_setting(
    id,base_url,enterprise_code,organization_code,user_code,client_id,client_secret_ciphertext,
    item_create_path,item_query_path,write_enabled,updated_by,updated_at)
VALUES (1,'http://10.7.7.188/U9','01','7','pdm','PDM','','/webapi/ItemMaster/Create','/webapi/ItemMaster/Query',0,'system',UTC_TIMESTAMP(6));
