ALTER TABLE project_validation_plan_item
    ADD COLUMN reviewer VARCHAR(100) NULL AFTER result;
