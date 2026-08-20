CREATE TEMPORARY TABLE pdm_unclassified_resequence AS
SELECT id,
       ROW_NUMBER() OVER (
           PARTITION BY project_id
           ORDER BY bom_kind, sequence_no, drawing_number, id
       ) AS new_sequence
FROM bom_item
WHERE is_pending_classification = 1;

UPDATE bom_item AS item
JOIN pdm_unclassified_resequence AS pending ON pending.id = item.id
SET item.bom_kind = 'Unclassified',
    item.sequence_no = pending.new_sequence,
    item.is_complete = 0,
    item.reconciliation_status = 'PendingClassification',
    item.reconciliation_note = COALESCE(
        NULLIF(item.reconciliation_note, ''),
        '图档源数据未填写有效的物料分类，等待人工归入标准件或非标件BOM。'
    );

DROP TEMPORARY TABLE pdm_unclassified_resequence;
