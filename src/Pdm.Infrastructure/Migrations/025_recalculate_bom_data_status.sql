UPDATE bom_item
SET is_complete = CASE
    WHEN TRIM(drawing_number) = ''
      OR TRIM(name) = ''
      OR TRIM(unit) = ''
      OR TRIM(revision_label) = '' THEN 0
    WHEN bom_kind = 'Standard' THEN specification IS NOT NULL AND TRIM(specification) <> ''
    WHEN bom_kind = 'NonStandard' THEN material IS NOT NULL AND TRIM(material) <> ''
    WHEN bom_kind = 'Electrical' THEN 1
    ELSE is_complete
END
WHERE bom_kind IN ('Standard', 'NonStandard', 'Electrical');
