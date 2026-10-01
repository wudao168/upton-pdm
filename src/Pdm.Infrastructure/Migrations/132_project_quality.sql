CREATE TABLE IF NOT EXISTS quality_validation_check_category LIKE validation_check_category;
CREATE TABLE IF NOT EXISTS quality_validation_check_item LIKE validation_check_item;
CREATE TABLE IF NOT EXISTS quality_project_validation_plan LIKE project_validation_plan;
CREATE TABLE IF NOT EXISTS quality_project_validation_plan_item LIKE project_validation_plan_item;
CREATE TABLE IF NOT EXISTS quality_validation_plan_approval_task LIKE validation_plan_approval_task;
CREATE TABLE IF NOT EXISTS quality_validation_plan_attachment LIKE validation_plan_attachment;
CREATE TABLE IF NOT EXISTS quality_validation_plan_execution_record LIKE validation_plan_execution_record;
CREATE TABLE IF NOT EXISTS quality_validation_plan_execution_item LIKE validation_plan_execution_item;

CREATE TABLE IF NOT EXISTS quality_inspection_record (
 id BINARY(16) NOT NULL PRIMARY KEY, project_id BINARY(16) NOT NULL,
 kind VARCHAR(20) NOT NULL, station VARCHAR(100) NOT NULL DEFAULT '', title VARCHAR(200) NOT NULL,
 remark VARCHAR(2000) NULL, file_name VARCHAR(255) NOT NULL, storage_relative_path VARCHAR(1000) NOT NULL,
 file_length BIGINT NOT NULL, sha256 VARCHAR(64) NOT NULL, uploaded_by VARCHAR(100) NOT NULL,
 uploaded_at DATETIME(6) NOT NULL, KEY ix_quality_inspection_project (project_id,kind,uploaded_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
INSERT IGNORE INTO folder_template_node(folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions) VALUES
('acceptance.quality-acceptance','acceptance','质量验收','Standard',20,1,1);
