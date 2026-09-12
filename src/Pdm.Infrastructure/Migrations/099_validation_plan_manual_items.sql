ALTER TABLE project_validation_plan_item
    DROP FOREIGN KEY fk_project_validation_plan_item_category,
    DROP FOREIGN KEY fk_project_validation_plan_item_catalog,
    MODIFY catalog_category_id BINARY(16) NULL,
    MODIFY catalog_item_id BINARY(16) NULL;
