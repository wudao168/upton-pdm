SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='assigned_reviewer'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN assigned_reviewer VARCHAR(100) NULL AFTER created_at');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='assigned_reviewer_name'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN assigned_reviewer_name VARCHAR(200) NULL AFTER assigned_reviewer');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='supervisor'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN supervisor VARCHAR(100) NULL AFTER assigned_reviewer_name');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='supervisor_name'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN supervisor_name VARCHAR(200) NULL AFTER supervisor');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='supervisor_reviewed_by'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN supervisor_reviewed_by VARCHAR(100) NULL AFTER supervisor_name');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='supervisor_reviewed_by_name'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN supervisor_reviewed_by_name VARCHAR(200) NULL AFTER supervisor_reviewed_by');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='supervisor_reviewed_at'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN supervisor_reviewed_at DATETIME(6) NULL AFTER supervisor_reviewed_by_name');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='supervisor_comment'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN supervisor_comment VARCHAR(2000) NULL AFTER supervisor_reviewed_at');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
VALUES
('MechanicalManager','drawing-review.annotate',UTC_TIMESTAMP(6)),
('MechanicalManager','drawing-review.decide',UTC_TIMESTAMP(6));
