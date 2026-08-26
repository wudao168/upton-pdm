ALTER TABLE organization_unit
    ADD COLUMN can_manufacture TINYINT(1) NOT NULL DEFAULT 0 AFTER kind;

UPDATE organization_unit
SET can_manufacture = 1
WHERE kind = 'BusinessDivision';
