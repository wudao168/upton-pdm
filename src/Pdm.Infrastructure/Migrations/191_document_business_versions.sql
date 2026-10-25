ALTER TABLE document_version ADD COLUMN drawing_version_number INT NULL;

-- Keep archive identifiers intact. Only work archives advance the drawing sequence.
UPDATE document_version v
JOIN (
    SELECT id, ROW_NUMBER() OVER (
        PARTITION BY document_id
        ORDER BY CASE WHEN revision_label LIKE 'W%' THEN 0 ELSE ASCII(LEFT(revision_label, 1)) END,
                 CAST(SUBSTRING_INDEX(revision_label, 'W', -1) AS UNSIGNED),
                 created_at, id
    ) AS drawing_number
    FROM document_version WHERE version_status = 'Work'
) numbered ON numbered.id = v.id
SET v.drawing_version_number = numbered.drawing_number;

UPDATE document_version released
JOIN document_version source ON source.id = released.source_version_id
    AND source.document_id = released.document_id AND source.version_status = 'Work'
SET released.drawing_version_number = source.drawing_version_number
WHERE released.version_status = 'Released';
