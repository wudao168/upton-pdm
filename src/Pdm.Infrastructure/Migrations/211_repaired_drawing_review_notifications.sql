-- 210 修复推进审核状态时未经过应用层通知。仅为这些单据补齐当前节点通知。
-- 已批准的单据只通知发起人批准结果，不向主管发送过期的批准待办。
CREATE TEMPORARY TABLE repaired_drawing_review_notice AS
SELECT package.id package_id, package.project_id, package.review_number, project.code project_code,
       package.state, package.created_by, package.supervisor,
       COALESCE(package.supervisor_reviewed_by_name,package.supervisor_name,package.supervisor) approver_name,
       COALESCE(MAX(item.drawing_reviewed_at),package.created_at) review_completed_at
FROM drawing_review_package package
JOIN project ON project.id=package.project_id
JOIN drawing_review_item item ON item.package_id=package.id
WHERE package.state IN ('PendingSupervisorApproval','WritingProperties','Approved')
  AND EXISTS (SELECT 1 FROM audit_entry audit WHERE audit.action_name='drawing-review.shared-drawing.repair'
      AND JSON_UNQUOTE(JSON_EXTRACT(audit.detail_json,'$.packageId'))=BIN_TO_UUID(package.id))
GROUP BY package.id,package.project_id,package.review_number,project.code,package.state,package.created_by,
         package.supervisor,package.supervisor_reviewed_by_name,package.supervisor_name,package.created_at;

INSERT IGNORE INTO user_notification
    (id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at)
SELECT UUID_TO_BIN(UUID()),repair.supervisor,'DrawingReviewSupervisorApproval','待机械主管批准',
       CONCAT(repair.project_code,' · ',repair.review_number,' 已完成审图，等待机械主管批准'),
       repair.project_id,NULL,CONCAT('drawing-review:',LOWER(HEX(repair.package_id)),':supervisor:',DATE_FORMAT(repair.review_completed_at,'%Y%m%d%H%i%s%f')),
       UTC_TIMESTAMP(6),NULL
FROM repaired_drawing_review_notice repair JOIN pdm_user recipient ON recipient.username=repair.supervisor AND recipient.is_active=1
WHERE repair.state='PendingSupervisorApproval'
  AND NOT EXISTS (SELECT 1 FROM user_notification notice WHERE notice.recipient_username=repair.supervisor
      AND notice.category='DrawingReviewSupervisorApproval'
      AND notice.source_key LIKE CONCAT('drawing-review:',LOWER(HEX(repair.package_id)),':supervisor%')
      AND notice.created_at>=repair.review_completed_at AND notice.resolved_at IS NULL);

INSERT IGNORE INTO user_notification
    (id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at,resolved_at)
SELECT UUID_TO_BIN(UUID()),repair.created_by,'DrawingReviewApproved','2D图纸已批准',
       CONCAT(repair.project_code,' · ',repair.review_number,' 已由 ',repair.approver_name,' 批准，审核标记按属性回写任务处理。'),
       repair.project_id,NULL,CONCAT('drawing-review:',LOWER(HEX(repair.package_id)),':approved'),UTC_TIMESTAMP(6),NULL,UTC_TIMESTAMP(6)
FROM repaired_drawing_review_notice repair JOIN pdm_user recipient ON recipient.username=repair.created_by AND recipient.is_active=1
WHERE repair.state IN ('WritingProperties','Approved');

DROP TEMPORARY TABLE repaired_drawing_review_notice;
