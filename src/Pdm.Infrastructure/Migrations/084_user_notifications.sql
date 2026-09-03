CREATE TABLE IF NOT EXISTS user_notification (
    id BINARY(16) NOT NULL,
    recipient_username VARCHAR(100) NOT NULL,
    category VARCHAR(80) NOT NULL,
    title VARCHAR(200) NOT NULL,
    content VARCHAR(1000) NOT NULL,
    project_id BINARY(16) NULL,
    release_package_id BINARY(16) NULL,
    source_key VARCHAR(400) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    read_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_user_notification_recipient_source (recipient_username, source_key),
    KEY ix_user_notification_recipient_read (recipient_username, read_at, created_at),
    CONSTRAINT fk_user_notification_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_user_notification_release_package FOREIGN KEY (release_package_id) REFERENCES release_package(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

UPDATE approval_task task
INNER JOIN release_package package ON package.id=task.release_package_id
SET task.decision_comment='退回'
WHERE package.package_number='RP-P700005-3-20260903-E7819CDE'
  AND task.decision_value='Rejected'
  AND TRIM(task.decision_comment)='同意';

INSERT IGNORE INTO user_notification(
    id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at)
SELECT UUID_TO_BIN(UUID()),recipients.username,'ReleaseApprovalRejected','BOM发布审批已退回',
       CONCAT(project.code,' · ',package.package_number,' 被 ',rejected.decision_by,' 退回：',COALESCE(NULLIF(TRIM(rejected.decision_comment),''),'退回')),
       package.project_id,package.id,
       CONCAT('release-package:',LOWER(HEX(package.id)),':rejected:',LOWER(HEX(rejected.id))),
       COALESCE(rejected.decided_at,package.created_at),NULL
FROM release_package package
INNER JOIN project ON project.id=package.project_id
INNER JOIN approval_task rejected ON rejected.release_package_id=package.id AND rejected.decision_value='Rejected'
INNER JOIN (
    SELECT release_package_id,decision_by AS username
    FROM approval_task
    WHERE decision_by IS NOT NULL
    UNION
    SELECT release_package_id,assignee AS username
    FROM approval_task
    WHERE step_order=1
    UNION
    SELECT package_for_manager.id AS release_package_id,assignment.username
    FROM release_package package_for_manager
    INNER JOIN project project_for_manager ON project_for_manager.id=package_for_manager.project_id
    INNER JOIN project_assignment assignment
        ON assignment.project_id IN (package_for_manager.project_id,COALESCE(project_for_manager.root_project_id,package_for_manager.project_id))
       AND assignment.assignment_type='PrimaryProjectManager'
) recipients ON recipients.release_package_id=package.id
WHERE package.package_number='RP-P700005-3-20260903-E7819CDE'
  AND recipients.username IS NOT NULL
  AND TRIM(recipients.username)<>'';
