ALTER TABLE release_package
    ADD COLUMN release_scope VARCHAR(60) NOT NULL DEFAULT 'LegacyCombined' AFTER state,
    ADD COLUMN workflow_code VARCHAR(100) NULL AFTER release_scope,
    ADD COLUMN workflow_version INT NOT NULL DEFAULT 0 AFTER workflow_code,
    ADD COLUMN selected_bom_item_ids_json JSON NULL AFTER workflow_version,
    ADD COLUMN creates_manufacturing_baseline TINYINT(1) NOT NULL DEFAULT 1 AFTER selected_bom_item_ids_json,
    ADD COLUMN locks_documents TINYINT(1) NOT NULL DEFAULT 1 AFTER creates_manufacturing_baseline;

ALTER TABLE approval_task
    ADD COLUMN step_order INT NOT NULL DEFAULT 0 AFTER stage,
    ADD COLUMN step_name VARCHAR(120) NULL AFTER step_order,
    ADD COLUMN is_emergency_substitute TINYINT(1) NOT NULL DEFAULT 0 AFTER decided_at,
    ADD COLUMN emergency_reason VARCHAR(1000) NULL AFTER is_emergency_substitute;

UPDATE approval_task SET step_order = CASE stage WHEN 'ProcessReview' THEN 1 WHEN 'Approval' THEN 2 ELSE 0 END;

UPDATE release_package SET selected_bom_item_ids_json = JSON_ARRAY() WHERE selected_bom_item_ids_json IS NULL;

ALTER TABLE release_package MODIFY selected_bom_item_ids_json JSON NOT NULL;

ALTER TABLE approval_task ADD UNIQUE KEY ux_approval_task_order (release_package_id, step_order);
