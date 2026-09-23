ALTER TABLE project_validation_plan_item
    ADD COLUMN validation_standard VARCHAR(1000) NULL AFTER validation_content;
