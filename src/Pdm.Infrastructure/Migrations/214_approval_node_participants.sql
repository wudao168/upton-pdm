SET @pdm_node_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns
    WHERE table_schema=DATABASE() AND table_name='approval_task' AND column_name='participants_json'),
    'SELECT 1', 'ALTER TABLE approval_task ADD COLUMN participants_json JSON NULL');
PREPARE pdm_node_column FROM @pdm_node_sql;
EXECUTE pdm_node_column;
DEALLOCATE PREPARE pdm_node_column;

SET @pdm_node_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns
    WHERE table_schema=DATABASE() AND table_name='validation_plan_approval_task' AND column_name='participants_json'),
    'SELECT 1', 'ALTER TABLE validation_plan_approval_task ADD COLUMN participants_json JSON NULL');
PREPARE pdm_node_column FROM @pdm_node_sql;
EXECUTE pdm_node_column;
DEALLOCATE PREPARE pdm_node_column;

SET @pdm_node_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns
    WHERE table_schema=DATABASE() AND table_name='quality_validation_plan_approval_task' AND column_name='participants_json'),
    'SELECT 1', 'ALTER TABLE quality_validation_plan_approval_task ADD COLUMN participants_json JSON NULL');
PREPARE pdm_node_column FROM @pdm_node_sql;
EXECUTE pdm_node_column;
DEALLOCATE PREPARE pdm_node_column;
