ALTER TABLE material_code_application ADD COLUMN approval_workflow_json JSON NULL;
ALTER TABLE release_package ADD COLUMN emergency_roles_json JSON NULL;
ALTER TABLE validation_plan_approval_task ADD COLUMN emergency_roles_json JSON NULL;
ALTER TABLE quality_validation_plan_approval_task ADD COLUMN emergency_roles_json JSON NULL;
