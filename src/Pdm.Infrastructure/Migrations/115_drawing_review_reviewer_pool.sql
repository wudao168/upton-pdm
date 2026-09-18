SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='assigned_reviewers'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN assigned_reviewers VARCHAR(2000) NULL AFTER assigned_reviewer_name');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='drawing_review_package' AND column_name='assigned_reviewer_names'), 'SELECT 1', 'ALTER TABLE drawing_review_package ADD COLUMN assigned_reviewer_names VARCHAR(4000) NULL AFTER assigned_reviewers');
PREPARE statement FROM @sql; EXECUTE statement; DEALLOCATE PREPARE statement;

UPDATE drawing_review_package
SET assigned_reviewers = JSON_ARRAY(assigned_reviewer)
WHERE assigned_reviewers IS NULL AND assigned_reviewer IS NOT NULL;
UPDATE drawing_review_package
SET assigned_reviewer_names = JSON_ARRAY(assigned_reviewer_name)
WHERE assigned_reviewer_names IS NULL AND assigned_reviewer_name IS NOT NULL;

INSERT INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES
('DrawingReviewer','审图员','负责机械图纸审核，可由任意具备审图权限的人员并行处理。','ProcessReviewer',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
    role_name=VALUES(role_name),
    description=VALUES(description),
    base_role=VALUES(base_role),
    is_system=1,
    updated_at=UTC_TIMESTAMP(6);

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
VALUES
('DrawingReviewer','project.view',UTC_TIMESTAMP(6)),
('DrawingReviewer','project.content.view',UTC_TIMESTAMP(6)),
('DrawingReviewer','drawing-review.annotate',UTC_TIMESTAMP(6)),
('DrawingReviewer','drawing-review.decide',UTC_TIMESTAMP(6));
