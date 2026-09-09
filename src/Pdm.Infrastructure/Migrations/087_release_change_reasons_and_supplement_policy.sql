ALTER TABLE release_package
    ADD COLUMN change_reason_selections_json JSON NULL AFTER change_reason,
    ADD COLUMN formal_supplement_policy_snapshotted TINYINT(1) NOT NULL DEFAULT 0 AFTER change_reason_selections_json,
    ADD COLUMN formal_supplement_maximum_count INT NULL AFTER formal_supplement_policy_snapshotted,
    ADD COLUMN formal_supplement_valid_days INT NULL AFTER formal_supplement_maximum_count;

UPDATE release_package
SET formal_supplement_policy_snapshotted = 1,
    formal_supplement_maximum_count = 2,
    formal_supplement_valid_days = NULL
WHERE release_scope IN ('StandardFormal', 'ElectricalFormal')
  AND state = 'Published';
