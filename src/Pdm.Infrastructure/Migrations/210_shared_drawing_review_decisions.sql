-- 同一审核单内同一冻结工程图版本只应有一份审核结论。
-- 仅修复一条真实通过记录及其未处理的模型关联项，不合并冲突结论或不同版本。
CREATE TEMPORARY TABLE shared_drawing_review_repair AS
SELECT approved.id source_id, pending.id item_id, pending.package_id,
       approved.drawing_reviewer, approved.drawing_reviewer_name,
       approved.drawing_reviewed_at, approved.drawing_comment
FROM drawing_review_item approved
JOIN drawing_review_package package ON package.id=approved.package_id AND package.state='InReview'
JOIN drawing_review_item pending ON pending.package_id=approved.package_id
    AND pending.drawing_document_id=approved.drawing_document_id
    AND pending.drawing_version_id=approved.drawing_version_id
    AND pending.drawing_sha256=approved.drawing_sha256 AND pending.drawing_state='Pending'
WHERE approved.drawing_state='Approved'
  AND approved.drawing_reviewer IS NOT NULL AND approved.drawing_reviewed_at IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM drawing_review_item other
      WHERE other.package_id=approved.package_id AND other.drawing_document_id=approved.drawing_document_id
        AND other.drawing_version_id=approved.drawing_version_id AND other.id<>approved.id
        AND other.drawing_state<>'Pending')
  AND NOT EXISTS (
      SELECT 1 FROM drawing_review_markup markup
      JOIN drawing_review_item marked ON marked.id=markup.item_id
      WHERE marked.package_id=approved.package_id AND marked.drawing_document_id=approved.drawing_document_id
        AND marked.drawing_version_id=approved.drawing_version_id
        AND markup.target='Drawing2D' AND markup.severity='Blocking' AND markup.state='Open');

INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
SELECT UUID_TO_BIN(UUID()),UTC_TIMESTAMP(6),'system','drawing-review.shared-drawing.repair','DrawingReviewItem',
       BIN_TO_UUID(item_id),JSON_OBJECT('detail','按同一冻结工程图已有审核结论同步模型关联项，保留原审核人及时间',
       'sourceItemId',BIN_TO_UUID(source_id),'previousState','Pending','packageId',BIN_TO_UUID(package_id))
FROM shared_drawing_review_repair;

UPDATE drawing_review_item item JOIN shared_drawing_review_repair repair ON repair.item_id=item.id
SET item.drawing_state='Approved',item.drawing_reviewer=repair.drawing_reviewer,
    item.drawing_reviewer_name=repair.drawing_reviewer_name,item.drawing_reviewed_at=repair.drawing_reviewed_at,
    item.drawing_comment=repair.drawing_comment;

UPDATE drawing_review_package package
SET package.state='PendingSupervisorApproval'
WHERE package.state='InReview' AND package.supervisor IS NOT NULL AND package.supervisor<>''
  AND package.id IN (SELECT package_id FROM shared_drawing_review_repair)
  AND NOT EXISTS (SELECT 1 FROM drawing_review_item item WHERE item.package_id=package.id AND item.drawing_state<>'Approved');

DROP TEMPORARY TABLE shared_drawing_review_repair;
